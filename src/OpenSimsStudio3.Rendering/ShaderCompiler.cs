using Silk.NET.Shaderc;

namespace OpenSimsStudio3.Rendering;

/// <summary>Compiles GLSL to SPIR-V at runtime with shaderc (bundled through Silk.NET.Shaderc.Native).</summary>
public static unsafe class ShaderCompiler
{
    static readonly Shaderc Api = Shaderc.GetApi();

    public static byte[] Compile(string glsl, ShaderKind kind, string name)
    {
        var compiler = Api.CompilerInitialize();
        var options = Api.CompileOptionsInitialize();
        try
        {
            var result = Api.CompileIntoSpv(compiler, glsl, (nuint)System.Text.Encoding.UTF8.GetByteCount(glsl), kind, name, "main", options);
            try
            {
                if (Api.ResultGetCompilationStatus(result) != CompilationStatus.Success)
                {
                    var message = Silk.NET.Core.Native.SilkMarshal.PtrToString((nint)Api.ResultGetErrorMessage(result));
                    throw new InvalidOperationException($"Shader {name} failed to compile: {message}");
                }
                var length = (int)Api.ResultGetLength(result);
                return new ReadOnlySpan<byte>(Api.ResultGetBytes(result), length).ToArray();
            }
            finally { Api.ResultRelease(result); }
        }
        finally
        {
            Api.CompileOptionsRelease(options);
            Api.CompilerRelease(compiler);
        }
    }
}
