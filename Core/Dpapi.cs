using System.Runtime.InteropServices;
using System.Text;

namespace MouseWheelEnhancement.Core;

/// <summary>
/// 用 Windows 自带的 DPAPI 保护凭据（GitHub 个人访问令牌）。
///
/// # 为什么不用自己写加密
///
/// 令牌必须是**可逆**的（程序要用它去请求 GitHub），所以哈希没用。自己写 AES
/// 就得在程序里藏一把密钥 —— 那等于没加密，只是换了个地方放明文。
/// DPAPI 的密钥由系统按「当前用户 + 当前机器」派生并保管，别的账户 / 别的机器
/// 拿到那份 config.json 也解不开，正是凭据该有的保护级别。
///
/// 代价：config.json 不能跨机器直接复制（令牌那一段解不开）。所以解密失败时
/// 一律**当作没填令牌**，不抛异常、不影响其它配置。
/// </summary>
internal static class Dpapi
{
    private const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    public static string? Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        var data = Encoding.UTF8.GetBytes(plain);
        var inBlob = new Native.DATA_BLOB();
        var outBlob = new Native.DATA_BLOB();
        var p = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.Copy(data, 0, p, data.Length);
            inBlob.cbData = data.Length;
            inBlob.pbData = p;

            var entropy = new Native.DATA_BLOB();
            if (!Native.CryptProtectData(ref inBlob, "MouseWheelEnhancement.GitHubToken", ref entropy,
                    IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                return null;

            try
            {
                var buf = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, buf, 0, outBlob.cbData);
                return Convert.ToBase64String(buf);
            }
            finally { Native.LocalFree(outBlob.pbData); }
        }
        catch { return null; }
        finally { Marshal.FreeHGlobal(p); }
    }

    public static string? Unprotect(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;
        byte[] buf;
        try { buf = Convert.FromBase64String(base64); }
        catch { return null; }

        var inBlob = new Native.DATA_BLOB();
        var outBlob = new Native.DATA_BLOB();
        var p = Marshal.AllocHGlobal(buf.Length);
        try
        {
            Marshal.Copy(buf, 0, p, buf.Length);
            inBlob.cbData = buf.Length;
            inBlob.pbData = p;

            var entropy = new Native.DATA_BLOB();
            if (!Native.CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropy,
                    IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                return null;

            try
            {
                var outBuf = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, outBuf, 0, outBlob.cbData);
                return Encoding.UTF8.GetString(outBuf);
            }
            finally { Native.LocalFree(outBlob.pbData); }
        }
        catch { return null; }
        finally { Marshal.FreeHGlobal(p); }
    }
}
