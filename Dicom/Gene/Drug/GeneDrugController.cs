using System;
using UnityEngine;

namespace Dicom.Gene
{
    // 药物状态中枢(mode3):唯一持有"当前用药 + 剂量"的地方,对外只发不可变快照
    // 通信设计:所有下游(显色/幽灵底图/画笔区域分析/各面板)一律订阅 OnStateChanged 拿 GeneDrugSnapshot,
    //   不反向读控制器字段、不互相直接调用,故新增面板/分析器无需改本类;
    //   快照带单调递增 Revision,异步任务(区域分析)完成后比对版本即可判定结果是否已被新用药作废
    // 剂量平滑:面板给的是目标剂量,本类每帧向目标插值并按固定间隔发布快照,
    //   使表达强度连续变化 -> 点云整体颜色平滑过渡而非跳变
    [RequireComponent(typeof(GeneColorController))]
    public class GeneDrugController : MonoBehaviour
    {
        [SerializeField] GeneDrugProfile _profile;
        // 剂量从当前值走到目标值的时长(秒);0=立即
        [SerializeField] float _transitionSeconds = 1.2f;
        // 过渡期间快照发布间隔(秒);每次发布触发一次点集重建,30Hz 足够顺滑又不过载
        [SerializeField] float _publishInterval = 1f / 30f;

        // 药物状态变化(选药/换药/剂量插值步进/停药),携带最新快照
        public event Action<GeneDrugSnapshot> OnStateChanged;

        // 当前选中药索引,-1 表示未用药
        int _index = -1;
        float _currentDose;
        float _targetDose;
        int _revision;
        float _publishTimer;
        bool _ramping;

        GeneDrugSnapshot _snapshot = GeneDrugSnapshot.None;
        GeneColorController _color;

        public GeneDrugProfile Profile => _profile;
        public int DrugCount => _profile != null ? _profile.Count : 0;
        public int CurrentIndex => _index;
        public GeneDrugDefinition CurrentDrug => _profile != null ? _profile.Get(_index) : null;
        public string CurrentDrugName => _snapshot.DrugName;
        public float CurrentDose => _currentDose;
        public float TargetDose => _targetDose;
        public bool IsTransitioning => _ramping;
        // 下游拿当前快照(晚绑定的面板/分析器不必等下一次事件)
        public GeneDrugSnapshot Snapshot => _snapshot;
        // 当前药的剂量上限,供面板配滑条;未用药回 1 避免滑条 min==max
        public float MaxDose
        {
            get
            {
                var d = CurrentDrug;
                return d != null && d.MaxDose > 0f ? d.MaxDose : 1f;
            }
        }

        // 显色控制器是本模块的固定下游:此处自行接线,面板/Bootstrap 无需知道两者关系
        // 订阅前先退订,项目禁用 Domain Reload,幂等才不会跨 PlayMode 会话叠加重复回调
        void Awake()
        {
            _color = GetComponent<GeneColorController>();
            OnStateChanged -= _color.SetDrugState;
            OnStateChanged += _color.SetDrugState;
        }

        void OnDestroy()
        {
            if (_color != null) OnStateChanged -= _color.SetDrugState;
        }

        // 由 Bootstrap 注入药物库(与 LUT/TagNameTable 同套注入方式)
        public void SetProfile(GeneDrugProfile profile)
        {
            _profile = profile;
            // 换库后索引失效,回到未用药
            ClearDrug();
        }

        // 选药:剂量从 0 平滑升到该药默认剂量,得"给药后整体显色渐变"
        public void SelectDrug(int index)
        {
            if (_profile == null) return;
            var def = _profile.Get(index);
            if (def == null) return;

            // 换药时从 0 起,避免上一味药的剂量被当作新药起点
            if (index != _index) _currentDose = 0f;
            _index = index;
            _targetDose = Mathf.Clamp(def.DefaultDose, 0f, def.MaxDose > 0f ? def.MaxDose : def.DefaultDose);
            BeginRamp();
        }

        public void SelectDrugByName(string name)
        {
            if (_profile == null || string.IsNullOrEmpty(name)) return;
            for (int i = 0; i < _profile.Count; i++)
                if (_profile.GetName(i) == name) { SelectDrug(i); return; }
        }

        // 调剂量(目标值),过渡到位期间持续发布快照
        public void SetDose(float dose)
        {
            var def = CurrentDrug;
            if (def == null) return;
            _targetDose = Mathf.Clamp(dose, 0f, def.MaxDose > 0f ? def.MaxDose : dose);
            BeginRamp();
        }

        // 停药:剂量平滑回 0,归零后清除选中药并发布基线快照
        public void ClearDrug()
        {
            if (_index < 0 && _currentDose <= 0f)
            {
                // 已是基线,仍发一次保证晚绑定的下游同步到无药状态
                _index = -1;
                _targetDose = 0f;
                _currentDose = 0f;
                Publish();
                return;
            }
            _targetDose = 0f;
            BeginRamp();
        }

        // 立即停药不做过渡(切模式/换模型等场景需要瞬时回到基线)
        public void ResetImmediate()
        {
            _index = -1;
            _currentDose = 0f;
            _targetDose = 0f;
            _ramping = false;
            Publish();
        }

        void BeginRamp()
        {
            _ramping = true;
            _publishTimer = 0f;
            // 立即发一帧,让面板读数与显色马上开始动
            Publish();
            if (_transitionSeconds <= 0f)
            {
                _currentDose = _targetDose;
                FinishRamp();
            }
        }

        void Update()
        {
            if (!_ramping) return;

            var def = CurrentDrug;
            float span = def != null && def.MaxDose > 0f ? def.MaxDose : 1f;
            // 按"跑满量程需 _transitionSeconds"换算速率,使不同量程的药过渡观感一致
            float speed = _transitionSeconds > 0f ? span / _transitionSeconds : float.MaxValue;
            _currentDose = Mathf.MoveTowards(_currentDose, _targetDose, speed * Time.unscaledDeltaTime);

            _publishTimer += Time.unscaledDeltaTime;
            bool arrived = Mathf.Approximately(_currentDose, _targetDose);
            if (arrived)
            {
                _currentDose = _targetDose;
                FinishRamp();
                return;
            }
            if (_publishTimer >= _publishInterval)
            {
                _publishTimer = 0f;
                Publish();
            }
        }

        void FinishRamp()
        {
            _ramping = false;
            // 剂量归零视为停药,清空选中药使快照回到基线
            if (_currentDose <= 0f) _index = -1;
            Publish();
        }

        // 生成并广播新快照;Revision 单调递增供下游判定过期
        void Publish()
        {
            var def = CurrentDrug;
            float effect = def != null ? def.EffectStrength(_currentDose) : 0f;
            _revision++;
            _snapshot = def != null && effect > 0f
                ? new GeneDrugSnapshot(def, _currentDose, effect, _revision)
                : new GeneDrugSnapshot(null, 0f, 0f, _revision);
            OnStateChanged?.Invoke(_snapshot);
        }
    }
}
