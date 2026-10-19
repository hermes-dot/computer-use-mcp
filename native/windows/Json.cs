// JSON 读写辅助。
//
// 为什么用 JavaScriptSerializer 而不是 System.Text.Json：
//   System.Text.Json 属于 .NET Core / NuGet 包，本机无法访问 NuGet；
//   而 JavaScriptSerializer 在 .NET Framework 自带的 System.Web.Extensions.dll 中，零依赖。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace CuHelper
{
    internal static class Json
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer
        {
            MaxJsonLength = int.MaxValue,
            RecursionLimit = 200,
        };

        public static string Write(object value) => Serializer.Serialize(value);

        /// <summary>把一行 JSON 解析成字典；解析失败抛出 JsonException。</summary>
        public static Dictionary<string, object> Parse(string line)
        {
            var parsed = Serializer.DeserializeObject(line) as Dictionary<string, object>;
            if (parsed == null) throw new JsonException("请求必须是 JSON 对象");
            return parsed;
        }

        // ---- 取值辅助：JavaScriptSerializer 会把数字解析成 int/decimal，需要宽松转换 ----

        public static string Str(IDictionary<string, object> map, string key, string fallback = null)
        {
            object v;
            if (map == null || !map.TryGetValue(key, out v) || v == null) return fallback;
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static int? Int(IDictionary<string, object> map, string key)
        {
            object v;
            if (map == null || !map.TryGetValue(key, out v) || v == null) return null;
            if (v is string s)
            {
                int parsed;
                return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : (int?)null;
            }
            return Convert.ToInt32(v, CultureInfo.InvariantCulture);
        }

        public static long? Long(IDictionary<string, object> map, string key)
        {
            object v;
            if (map == null || !map.TryGetValue(key, out v) || v == null) return null;
            if (v is string s)
            {
                long parsed;
                return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : (long?)null;
            }
            return Convert.ToInt64(v, CultureInfo.InvariantCulture);
        }

        public static double? Double(IDictionary<string, object> map, string key)
        {
            object v;
            if (map == null || !map.TryGetValue(key, out v) || v == null) return null;
            if (v is string s)
            {
                double parsed;
                return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : (double?)null;
            }
            return Convert.ToDouble(v, CultureInfo.InvariantCulture);
        }

        public static bool Bool(IDictionary<string, object> map, string key, bool fallback = false)
        {
            object v;
            if (map == null || !map.TryGetValue(key, out v) || v == null) return fallback;
            if (v is bool b) return b;
            bool parsed;
            return bool.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out parsed) ? parsed : fallback;
        }

        public static IDictionary<string, object> Map(IDictionary<string, object> map, string key)
        {
            object v;
            if (map == null || !map.TryGetValue(key, out v)) return null;
            return v as IDictionary<string, object>;
        }

        /// <summary>
        /// 构造字段顺序稳定的字典（便于人读日志与测试断言）。
        /// 两种用法：<c>Obj()</c> 得到空字典；<c>Obj("k1", v1, "k2", v2)</c> 得到带初值的字典。
        /// </summary>
        public static Dictionary<string, object> Obj(params object[] pairs)
        {
            var map = new Dictionary<string, object>(StringComparer.Ordinal);
            for (int i = 0; i + 1 < pairs.Length; i += 2)
            {
                map[(string)pairs[i]] = pairs[i + 1];
            }
            return map;
        }
    }

    internal sealed class JsonException : Exception
    {
        public JsonException(string message) : base(message) { }
    }

    /// <summary>把 helper 的失败信息结构化返回给 Node 侧。</summary>
    internal sealed class HelperException : Exception
    {
        public string Code { get; }

        public HelperException(string code, string message) : base(message)
        {
            Code = code;
        }
    }
}
