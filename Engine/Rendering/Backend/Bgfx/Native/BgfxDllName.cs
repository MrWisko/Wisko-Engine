// Supported override from bgfx_dllname.cs ("define your own partial class ... with a const DllName"):
// "bgfx" resolves to libbgfx.dylib on macOS and bgfx.dll on Windows via .NET's native probing.
namespace Bgfx
{
public static partial class bgfx
{
       const string DllName = "bgfx";
}
}
