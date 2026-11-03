namespace OpenSimsStudio3.Rendering;

/// <summary>
/// Viewport shaders. Prototype lighting only (one key light plus a sky/ground fill), not the game's shading:
/// matching retail SimSkin and friends comes later. lightDirection.w = 1 samples the bound texture as albedo: a
/// composited recolour, premultiplied, laid over a flat skin tone where its alpha is below 1 (a top's bare arms and
/// neck), as the game layers clothing over skin. w = 2 is the flat skin tone alone (the Whole Sim body); w = 0 grey.
/// w = 3 (thumbnails) cuts the texture out where its alpha is below one half, with no skin underneath.
/// </summary>
static class Shaders
{
    // The view-projection matrix arrives in System.Numerics memory order (row vectors). Read as a GLSL column-major
    // mat4 that is its transpose, so matrix * vector here equals vector * matrix on the C# side.
    public const string MeshVertex = """
        #version 450
        layout(location = 0) in vec3 inPosition;
        layout(location = 1) in vec3 inNormal;
        layout(location = 2) in vec2 inUv;
        layout(push_constant) uniform Push { mat4 viewProjection; vec4 lightDirection; } pc;
        layout(location = 0) out vec3 vNormal;
        layout(location = 1) out vec2 vUv;
        void main()
        {
            gl_Position = pc.viewProjection * vec4(inPosition, 1.0);
            vNormal = inNormal;
            vUv = inUv;
        }
        """;

    public const string MeshFragment = """
        #version 450
        layout(location = 0) in vec3 vNormal;
        layout(location = 1) in vec2 vUv;
        layout(push_constant) uniform Push { mat4 viewProjection; vec4 lightDirection; } pc;
        layout(set = 0, binding = 0) uniform sampler2D albedoMap;
        layout(location = 0) out vec4 outColor;
        void main()
        {
            vec3 n = normalize(vNormal);
            if (!gl_FrontFacing) n = -n;
            float key = max(dot(n, pc.lightDirection.xyz), 0.0);
            float sky = 0.5 + 0.5 * n.y;
            const vec3 skin = vec3(0.78, 0.62, 0.52);
            vec4 texel = texture(albedoMap, vUv);
            vec3 albedo;
            if (pc.lightDirection.w > 2.5)
            {
                if (texel.a < 0.5) discard;
                albedo = texel.rgb / texel.a;
            }
            else albedo = pc.lightDirection.w > 1.5 ? skin
                        : pc.lightDirection.w > 0.5 ? texel.rgb + skin * (1.0 - texel.a) : vec3(0.80, 0.78, 0.75);
            vec3 color = albedo * (0.18 + 0.62 * key) + vec3(0.10, 0.11, 0.14) * sky;
            outColor = vec4(color, 1.0);
        }
        """;
}
