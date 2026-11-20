using System;

namespace MHPerformanceAnalyzer
{
    /// <summary>
    /// Preallocated frame-time histogram: 0.25 ms buckets up to 250 ms, 5 ms buckets up to 2000 ms,
    /// one overflow bucket. Percentiles are bucket upper bounds (accurate to 0.25 ms below 250 ms).
    /// </summary>
    internal sealed class Histogram
    {
        private const int FineBuckets = 1000;
        private const int CoarseBuckets = 350;
        private readonly long[] counts = new long[FineBuckets + CoarseBuckets + 1];

        public long Count { get; private set; }
        public double Sum { get; private set; }
        public float Max { get; private set; }

        public void Add(float ms)
        {
            int i;
            if (ms < 250f)
            {
                i = ms <= 0f ? 0 : (int)(ms * 4f);
            }
            else
            {
                i = FineBuckets + Math.Min((int)((ms - 250f) / 5f), CoarseBuckets);
            }
            counts[i]++;
            Count++;
            Sum += ms;
            if (ms > Max)
            {
                Max = ms;
            }
        }

        /// <summary>Takes a frame back out (world teardown frames). Max falls back to the highest remaining bucket.</summary>
        public void Remove(float ms)
        {
            int i = Index(ms);
            if (counts[i] == 0)
            {
                return;
            }
            counts[i]--;
            Count--;
            Sum -= ms;
            if (ms >= Max)
            {
                Max = 0;
                for (int k = counts.Length - 1; k >= 0; k--)
                {
                    if (counts[k] > 0)
                    {
                        Max = k < FineBuckets ? (k + 1) * 0.25f : 250f + (k - FineBuckets + 1) * 5f;
                        break;
                    }
                }
            }
        }

        private static int Index(float ms)
        {
            if (ms < 250f)
            {
                return ms <= 0f ? 0 : (int)(ms * 4f);
            }
            return FineBuckets + Math.Min((int)((ms - 250f) / 5f), CoarseBuckets);
        }

        public double Average => Count == 0 ? 0 : Sum / Count;

        public float Percentile(double p)
        {
            if (Count == 0)
            {
                return 0f;
            }
            long target = (long)Math.Ceiling(p * Count);
            if (target < 1)
            {
                target = 1;
            }
            long seen = 0;
            for (int i = 0; i < counts.Length; i++)
            {
                seen += counts[i];
                if (seen >= target)
                {
                    float upper = i < FineBuckets ? (i + 1) * 0.25f : 250f + (i - FineBuckets + 1) * 5f;
                    return Math.Min(upper, Max);
                }
            }
            return Max;
        }

        /// <summary>Number of frames at or above the given ms (bucket resolution).</summary>
        public long CountAbove(float ms)
        {
            int start = ms < 250f ? (int)(ms * 4f) : FineBuckets + Math.Min((int)((ms - 250f) / 5f), CoarseBuckets);
            long n = 0;
            for (int i = start; i < counts.Length; i++)
            {
                n += counts[i];
            }
            return n;
        }
    }
}
