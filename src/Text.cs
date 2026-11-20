using System.Globalization;
using System.Text;

namespace MHPerformanceAnalyzer
{
    /// <summary>Invariant-culture number formatting and JSON string escaping.</summary>
    internal static class Text
    {
        public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string F(double v, string format = "0.00") => v.ToString(format, Inv);

        public static string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.0", Inv);

        public static string Json(string? s)
        {
            if (s == null)
            {
                return "null";
            }
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", Inv));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
