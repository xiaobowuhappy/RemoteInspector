using System.Collections.Generic;

namespace Coplaydev.Performance
{
    internal sealed class CompositeStackSampler : IStackSampler, System.IDisposable
    {
        private readonly IStackSampler[] _samplers;
        private readonly bool[] _disabled;
        public string Name => "composite";
        public bool IsSupported
        {
            get
            {
                for (int i = 0; i < _samplers.Length; i++)
                {
                    if (_disabled[i]) continue;
                    try { if (_samplers[i].IsSupported) return true; }
                    catch { _disabled[i] = true; }
                }
                return false;
            }
        }

        public CompositeStackSampler(params IStackSampler[] samplers)
        {
            _samplers = samplers;
            _disabled = new bool[samplers.Length];
        }

        public void RegisterMainThread()
        {
            for (var i = 0; i < _samplers.Length; i++)
                Invoke(i, s => s.RegisterMainThread());
        }

        public void BeginStall(long generation, long monotonicMs)
        {
            for (var i = 0; i < _samplers.Length; i++)
                Invoke(i, s => s.BeginStall(generation, monotonicMs));
        }

        public void Sample(long generation, long monotonicMs)
        {
            for (var i = 0; i < _samplers.Length; i++)
                Invoke(i, s => s.Sample(generation, monotonicMs));
        }

        public List<StackSample> EndStall(long generation, long monotonicMs)
        {
            var result = new List<StackSample>();
            for (var i = 0; i < _samplers.Length; i++)
            {
                if (_disabled[i]) continue;
                try
                {
                    if (!_samplers[i].IsSupported) continue;
                    var samples = _samplers[i].EndStall(generation, monotonicMs);
                    if (samples != null) result.AddRange(samples);
                }
                catch { _disabled[i] = true; }
            }
            return result;
        }

        private void Invoke(int index, System.Action<IStackSampler> action)
        {
            if (_disabled[index]) return;
            try
            {
                if (!_samplers[index].IsSupported) return;
                action(_samplers[index]);
            }
            catch { _disabled[index] = true; }
        }

        public void Dispose()
        {
            for (var i = 0; i < _samplers.Length; i++)
            {
                var disposable = _samplers[i] as System.IDisposable;
                if (disposable == null) continue;
                try { disposable.Dispose(); } catch { }
            }
        }
    }
}
