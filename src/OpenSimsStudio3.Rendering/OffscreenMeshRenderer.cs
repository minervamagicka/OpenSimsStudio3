using System.Numerics;
using System.Runtime.InteropServices;
using OpenSimsStudio3.Formats.Meshes;
using Silk.NET.Core.Native;
using Silk.NET.Shaderc;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace OpenSimsStudio3.Rendering;

/// <summary>
/// Draws one mesh with Vulkan into an offscreen image and copies the pixels back to the CPU (BGRA, top row first).
/// Prototype path: the UI shows the copied pixels. A zero-copy path through Avalonia's GPU interop can replace the
/// readback later without changing callers.
/// </summary>
public sealed unsafe class OffscreenMeshRenderer : IDisposable
{
    const Format ColorFormat = Format.B8G8R8A8Unorm; // byte order matches Avalonia's Bgra8888
    const Format DepthFormat = Format.D32Sfloat;
    const int VertexStride = 8 * sizeof(float);      // position + normal + uv

    [StructLayout(LayoutKind.Sequential)]
    struct PushConstants
    {
        public Matrix4x4 ViewProjection;
        public Vector4 LightDirection;
    }

    readonly Vk _vk = Vk.GetApi();
    readonly Instance _instance;
    readonly PhysicalDevice _physicalDevice;
    readonly Device _device;
    readonly Queue _queue;
    readonly CommandPool _commandPool;
    readonly CommandBuffer _commandBuffer;
    readonly Fence _fence;
    readonly RenderPass _renderPass;
    readonly PipelineLayout _pipelineLayout;
    readonly Pipeline _pipeline;
    readonly DescriptorSetLayout _descriptorLayout;
    readonly DescriptorPool _descriptorPool;
    readonly DescriptorSet _descriptorSet;
    readonly Sampler _sampler;

    // The albedo texture: a composited recolour, or a 1x1 white placeholder so the descriptor is always valid.
    Image _textureImage;
    DeviceMemory _textureMemory;
    ImageView _textureView;
    bool _hasTexture;

    // Size-dependent targets, rebuilt when the viewport size changes.
    int _width, _height;
    Image _colorImage, _depthImage;
    DeviceMemory _colorMemory, _depthMemory;
    ImageView _colorView, _depthView;
    Framebuffer _framebuffer;
    Buffer _readback;
    DeviceMemory _readbackMemory;

    // Mesh buffers.
    Buffer _vertexBuffer, _indexBuffer;
    DeviceMemory _vertexMemory, _indexMemory;
    uint _indexCount, _partIndexCount;

    public string DeviceName { get; }

    /// <summary>Background colour (linear RGBA as written to the BGRA target); the viewport's grey by default.</summary>
    public Vector4 ClearColor { get; set; } = new(0.17f, 0.18f, 0.20f, 1f);

    public OffscreenMeshRenderer()
    {
        _instance = CreateInstance();
        (_physicalDevice, DeviceName, var family) = PickDevice();
        _device = CreateDevice(family);
        _vk.GetDeviceQueue(_device, family, 0, out var queue);
        _queue = queue;

        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = family,
        };
        Check(_vk.CreateCommandPool(_device, &poolInfo, null, out _commandPool), "command pool");
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        Check(_vk.AllocateCommandBuffers(_device, &allocInfo, out _commandBuffer), "command buffer");
        var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
        Check(_vk.CreateFence(_device, &fenceInfo, null, out _fence), "fence");

        _renderPass = CreateRenderPass();
        (_descriptorLayout, _descriptorPool, _descriptorSet, _sampler) = CreateTextureBinding();
        (_pipelineLayout, _pipeline) = CreatePipeline();
        UploadTexture(1, 1, [255, 255, 255, 255]);
        _hasTexture = false;
    }

    /// <summary>Uses RGBA8 pixels (top row first) as the mesh's albedo, e.g. a composited recolour.</summary>
    public void SetTexture(int width, int height, ReadOnlySpan<byte> rgba)
    {
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4) throw new ArgumentException("bad texture size", nameof(rgba));
        UploadTexture(width, height, rgba);
        _hasTexture = true;
    }

    /// <summary>Back to the untextured grey.</summary>
    public void ClearTexture() => _hasTexture = false;

    /// <summary>
    /// With a texture bound, drop texels under half alpha instead of laying them over skin (rendered thumbnails show
    /// the item alone).
    /// </summary>
    public bool AlphaCutout { get; set; }

    /// <summary>
    /// Uploads a mesh, replacing the previous one. <paramref name="context" /> (the rest of the Sim) shares the buffers
    /// and is drawn after it, untextured and skin-tinted.
    /// </summary>
    public void SetMesh(GeomMesh part, GeomMesh? context = null)
    {
        _vk.DeviceWaitIdle(_device);
        DestroyMeshBuffers();
        var mesh = context == null ? part : GeomMesh.Merge(part.Name, [part, context]);
        var vertices = new float[mesh.Positions.Length * 8];
        var hasUvs = mesh.Uvs.Length == mesh.Positions.Length;
        for (var i = 0; i < mesh.Positions.Length; i++)
        {
            var (p, n) = (mesh.Positions[i], mesh.Normals[i]);
            vertices[8 * i] = p.X; vertices[8 * i + 1] = p.Y; vertices[8 * i + 2] = p.Z;
            vertices[8 * i + 3] = n.X; vertices[8 * i + 4] = n.Y; vertices[8 * i + 5] = n.Z;
            if (hasUvs) { vertices[8 * i + 6] = mesh.Uvs[i].X; vertices[8 * i + 7] = mesh.Uvs[i].Y; }
        }
        (_vertexBuffer, _vertexMemory) = CreateHostBuffer(MemoryMarshal.AsBytes(vertices.AsSpan()), BufferUsageFlags.VertexBufferBit);
        (_indexBuffer, _indexMemory) = CreateHostBuffer(MemoryMarshal.AsBytes(mesh.Indices.AsSpan()), BufferUsageFlags.IndexBufferBit);
        _indexCount = (uint)mesh.Indices.Length;
        _partIndexCount = (uint)part.Indices.Length;
    }

    /// <summary>Renders one frame and writes width x height BGRA pixels (top row first) into <paramref name="destination"/>.</summary>
    public void Render(int width, int height, Matrix4x4 viewProjection, Vector3 lightDirection, Span<byte> destination)
    {
        if (width <= 0 || height <= 0) return;
        if (destination.Length < width * height * 4) throw new ArgumentException("destination too small", nameof(destination));
        if (width != _width || height != _height) CreateTargets(width, height);

        Check(_vk.ResetCommandBuffer(_commandBuffer, 0), "reset command buffer");
        var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        Check(_vk.BeginCommandBuffer(_commandBuffer, &begin), "begin command buffer");

        var clears = stackalloc ClearValue[2];
        clears[0].Color = new ClearColorValue(ClearColor.X, ClearColor.Y, ClearColor.Z, ClearColor.W);
        clears[1].DepthStencil = new ClearDepthStencilValue(1f, 0);
        var passBegin = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _renderPass,
            Framebuffer = _framebuffer,
            RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)width, (uint)height)),
            ClearValueCount = 2,
            PClearValues = clears,
        };
        _vk.CmdBeginRenderPass(_commandBuffer, &passBegin, SubpassContents.Inline);
        if (_indexCount > 0)
        {
            _vk.CmdBindPipeline(_commandBuffer, PipelineBindPoint.Graphics, _pipeline);
            var viewport = new Viewport(0, 0, width, height, 0, 1);
            _vk.CmdSetViewport(_commandBuffer, 0, 1, &viewport);
            var scissor = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)width, (uint)height));
            _vk.CmdSetScissor(_commandBuffer, 0, 1, &scissor);
            var push = new PushConstants { ViewProjection = viewProjection, LightDirection = new Vector4(Vector3.Normalize(lightDirection), _hasTexture ? (AlphaCutout ? 3 : 1) : 0) };
            var set = _descriptorSet;
            _vk.CmdBindDescriptorSets(_commandBuffer, PipelineBindPoint.Graphics, _pipelineLayout, 0, 1, &set, 0, null);
            _vk.CmdPushConstants(_commandBuffer, _pipelineLayout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)sizeof(PushConstants), &push);
            var vertexBuffer = _vertexBuffer;
            ulong offset = 0;
            _vk.CmdBindVertexBuffers(_commandBuffer, 0, 1, &vertexBuffer, &offset);
            _vk.CmdBindIndexBuffer(_commandBuffer, _indexBuffer, 0, IndexType.Uint32);
            _vk.CmdDrawIndexed(_commandBuffer, _partIndexCount, 1, 0, 0, 0);
            if (_indexCount > _partIndexCount)
            {
                push.LightDirection.W = 2;
                _vk.CmdPushConstants(_commandBuffer, _pipelineLayout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)sizeof(PushConstants), &push);
                _vk.CmdDrawIndexed(_commandBuffer, _indexCount - _partIndexCount, 1, _partIndexCount, 0, 0);
            }
        }
        _vk.CmdEndRenderPass(_commandBuffer);

        // The render pass leaves the colour image in TransferSrcOptimal; copy it into the readback buffer.
        var copy = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D((uint)width, (uint)height, 1),
        };
        _vk.CmdCopyImageToBuffer(_commandBuffer, _colorImage, ImageLayout.TransferSrcOptimal, _readback, 1, &copy);
        Check(_vk.EndCommandBuffer(_commandBuffer), "end command buffer");

        var commandBuffer = _commandBuffer;
        var submit = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &commandBuffer };
        Check(_vk.QueueSubmit(_queue, 1, &submit, _fence), "queue submit");
        var fence = _fence;
        Check(_vk.WaitForFences(_device, 1, &fence, true, ulong.MaxValue), "wait for frame");
        Check(_vk.ResetFences(_device, 1, &fence), "reset fence");

        void* mapped;
        var size = (ulong)(width * height * 4);
        Check(_vk.MapMemory(_device, _readbackMemory, 0, size, 0, &mapped), "map readback");
        new ReadOnlySpan<byte>(mapped, (int)size).CopyTo(destination);
        _vk.UnmapMemory(_device, _readbackMemory);
    }

    // ---- setup -------------------------------------------------------------------------------------------------

    Instance CreateInstance()
    {
        var appName = (byte*)SilkMarshal.StringToPtr("OpenSimsStudio3");
        var appInfo = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = appName,
            PEngineName = appName,
            ApiVersion = Vk.Version12,
        };
        var info = new InstanceCreateInfo { SType = StructureType.InstanceCreateInfo, PApplicationInfo = &appInfo };
        try
        {
            Check(_vk.CreateInstance(&info, null, out var instance), "Vulkan instance (is a Vulkan driver installed?)");
            return instance;
        }
        finally { SilkMarshal.Free((nint)appName); }
    }

    (PhysicalDevice Device, string Name, uint QueueFamily) PickDevice()
    {
        uint count = 0;
        Check(_vk.EnumeratePhysicalDevices(_instance, &count, null), "enumerate GPUs");
        if (count == 0) throw new InvalidOperationException("No Vulkan-capable GPU found.");
        var devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* p = devices) Check(_vk.EnumeratePhysicalDevices(_instance, &count, p), "enumerate GPUs");

        (PhysicalDevice, string, uint)? best = null;
        var bestScore = -1;
        foreach (var device in devices)
        {
            _vk.GetPhysicalDeviceProperties(device, out var props);
            uint familyCount = 0;
            _vk.GetPhysicalDeviceQueueFamilyProperties(device, &familyCount, null);
            var families = new QueueFamilyProperties[familyCount];
            fixed (QueueFamilyProperties* f = families) _vk.GetPhysicalDeviceQueueFamilyProperties(device, &familyCount, f);
            var family = Array.FindIndex(families, f => f.QueueFlags.HasFlag(QueueFlags.GraphicsBit));
            if (family < 0) continue;
            var score = props.DeviceType switch
            {
                PhysicalDeviceType.DiscreteGpu => 3,
                PhysicalDeviceType.IntegratedGpu => 2,
                PhysicalDeviceType.VirtualGpu => 1,
                _ => 0,
            };
            if (score > bestScore)
            {
                bestScore = score;
                best = (device, SilkMarshal.PtrToString((nint)props.DeviceName) ?? "GPU", (uint)family);
            }
        }
        return best ?? throw new InvalidOperationException("No GPU with a graphics queue found.");
    }

    Device CreateDevice(uint family)
    {
        var priority = 1f;
        var queueInfo = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = family,
            QueueCount = 1,
            PQueuePriorities = &priority,
        };
        var info = new DeviceCreateInfo { SType = StructureType.DeviceCreateInfo, QueueCreateInfoCount = 1, PQueueCreateInfos = &queueInfo };
        Check(_vk.CreateDevice(_physicalDevice, &info, null, out var device), "logical device");
        return device;
    }

    RenderPass CreateRenderPass()
    {
        var attachments = stackalloc AttachmentDescription[2];
        attachments[0] = new AttachmentDescription
        {
            Format = ColorFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.TransferSrcOptimal,
        };
        attachments[1] = new AttachmentDescription
        {
            Format = DepthFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.DontCare,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
        };
        var colorRef = new AttachmentReference(0, ImageLayout.ColorAttachmentOptimal);
        var depthRef = new AttachmentReference(1, ImageLayout.DepthStencilAttachmentOptimal);
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorRef,
            PDepthStencilAttachment = &depthRef,
        };
        // Make the colour writes visible to the copy that follows the pass.
        var dependency = new SubpassDependency
        {
            SrcSubpass = 0,
            DstSubpass = Vk.SubpassExternal,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            DstStageMask = PipelineStageFlags.TransferBit,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
            DstAccessMask = AccessFlags.TransferReadBit,
        };
        var info = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 2,
            PAttachments = attachments,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 1,
            PDependencies = &dependency,
        };
        Check(_vk.CreateRenderPass(_device, &info, null, out var pass), "render pass");
        return pass;
    }

    (PipelineLayout, Pipeline) CreatePipeline()
    {
        var vertexModule = CreateShaderModule(ShaderCompiler.Compile(Shaders.MeshVertex, ShaderKind.VertexShader, "mesh.vert"));
        var fragmentModule = CreateShaderModule(ShaderCompiler.Compile(Shaders.MeshFragment, ShaderKind.FragmentShader, "mesh.frag"));
        var entry = (byte*)SilkMarshal.StringToPtr("main");
        try
        {
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = vertexModule, PName = entry };
            stages[1] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = fragmentModule, PName = entry };

            var binding = new VertexInputBindingDescription(0, VertexStride, VertexInputRate.Vertex);
            var attributes = stackalloc VertexInputAttributeDescription[3];
            attributes[0] = new VertexInputAttributeDescription(0, 0, Format.R32G32B32Sfloat, 0);
            attributes[1] = new VertexInputAttributeDescription(1, 0, Format.R32G32B32Sfloat, 3 * sizeof(float));
            attributes[2] = new VertexInputAttributeDescription(2, 0, Format.R32G32Sfloat, 6 * sizeof(float));
            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 1,
                PVertexBindingDescriptions = &binding,
                VertexAttributeDescriptionCount = 3,
                PVertexAttributeDescriptions = attributes,
            };
            var inputAssembly = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList };
            var viewportState = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
            // No culling: CC meshes come with either winding, and the shader lights both sides.
            var raster = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill,
                CullMode = CullModeFlags.None,
                FrontFace = FrontFace.CounterClockwise,
                LineWidth = 1,
            };
            var multisample = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit };
            var depth = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = true,
                DepthWriteEnable = true,
                DepthCompareOp = CompareOp.Less,
            };
            var blendAttachment = new PipelineColorBlendAttachmentState
            {
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            };
            var blend = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &blendAttachment };
            var dynamicStates = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
            var dynamic = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynamicStates };

            var pushRange = new PushConstantRange(ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)sizeof(PushConstants));
            var setLayout = _descriptorLayout;
            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo, PushConstantRangeCount = 1, PPushConstantRanges = &pushRange,
                SetLayoutCount = 1, PSetLayouts = &setLayout,
            };
            Check(_vk.CreatePipelineLayout(_device, &layoutInfo, null, out var layout), "pipeline layout");

            var info = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = 2,
                PStages = stages,
                PVertexInputState = &vertexInput,
                PInputAssemblyState = &inputAssembly,
                PViewportState = &viewportState,
                PRasterizationState = &raster,
                PMultisampleState = &multisample,
                PDepthStencilState = &depth,
                PColorBlendState = &blend,
                PDynamicState = &dynamic,
                Layout = layout,
                RenderPass = _renderPass,
                Subpass = 0,
            };
            Check(_vk.CreateGraphicsPipelines(_device, default, 1, &info, null, out var pipeline), "graphics pipeline");
            return (layout, pipeline);
        }
        finally
        {
            SilkMarshal.Free((nint)entry);
            _vk.DestroyShaderModule(_device, vertexModule, null);
            _vk.DestroyShaderModule(_device, fragmentModule, null);
        }
    }

    (DescriptorSetLayout, DescriptorPool, DescriptorSet, Sampler) CreateTextureBinding()
    {
        var binding = new DescriptorSetLayoutBinding(0, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit);
        var layoutInfo = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 1, PBindings = &binding };
        Check(_vk.CreateDescriptorSetLayout(_device, &layoutInfo, null, out var layout), "descriptor set layout");
        var size = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 1);
        var poolInfo = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 1, PoolSizeCount = 1, PPoolSizes = &size };
        Check(_vk.CreateDescriptorPool(_device, &poolInfo, null, out var pool), "descriptor pool");
        var allocInfo = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = pool, DescriptorSetCount = 1, PSetLayouts = &layout };
        Check(_vk.AllocateDescriptorSets(_device, &allocInfo, out var set), "descriptor set");
        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo, MagFilter = Filter.Linear, MinFilter = Filter.Linear, MipmapMode = SamplerMipmapMode.Linear,
            AddressModeU = SamplerAddressMode.Repeat, AddressModeV = SamplerAddressMode.Repeat, AddressModeW = SamplerAddressMode.Repeat, MaxLod = 0,
        };
        Check(_vk.CreateSampler(_device, &samplerInfo, null, out var sampler), "sampler");
        return (layout, pool, set, sampler);
    }

    /// <summary>Replaces the albedo texture: staging copy, layout transitions, then the descriptor points at it.</summary>
    void UploadTexture(int width, int height, ReadOnlySpan<byte> rgba)
    {
        _vk.DeviceWaitIdle(_device);
        DestroyTexture();
        (_textureImage, _textureMemory, _textureView) = CreateImage(width, height, Format.R8G8B8A8Unorm,
            ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit);
        var (staging, stagingMemory) = CreateHostBuffer(rgba[..(width * height * 4)], BufferUsageFlags.TransferSrcBit);
        try
        {
            Check(_vk.ResetCommandBuffer(_commandBuffer, 0), "reset command buffer");
            var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
            Check(_vk.BeginCommandBuffer(_commandBuffer, &begin), "begin upload");
            var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
            var toTransfer = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier, OldLayout = ImageLayout.Undefined, NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored, Image = _textureImage, SubresourceRange = range,
                DstAccessMask = AccessFlags.TransferWriteBit,
            };
            _vk.CmdPipelineBarrier(_commandBuffer, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, &toTransfer);
            var copy = new BufferImageCopy { ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1), ImageExtent = new Extent3D((uint)width, (uint)height, 1) };
            _vk.CmdCopyBufferToImage(_commandBuffer, staging, _textureImage, ImageLayout.TransferDstOptimal, 1, &copy);
            var toShader = toTransfer;
            toShader.OldLayout = ImageLayout.TransferDstOptimal;
            toShader.NewLayout = ImageLayout.ShaderReadOnlyOptimal;
            toShader.SrcAccessMask = AccessFlags.TransferWriteBit;
            toShader.DstAccessMask = AccessFlags.ShaderReadBit;
            _vk.CmdPipelineBarrier(_commandBuffer, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit, 0, 0, null, 0, null, 1, &toShader);
            Check(_vk.EndCommandBuffer(_commandBuffer), "end upload");
            var commandBuffer = _commandBuffer;
            var submit = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &commandBuffer };
            Check(_vk.QueueSubmit(_queue, 1, &submit, _fence), "submit upload");
            var fence = _fence;
            Check(_vk.WaitForFences(_device, 1, &fence, true, ulong.MaxValue), "wait for upload");
            Check(_vk.ResetFences(_device, 1, &fence), "reset fence");
        }
        finally
        {
            _vk.DestroyBuffer(_device, staging, null);
            _vk.FreeMemory(_device, stagingMemory, null);
        }
        var imageInfo = new DescriptorImageInfo(_sampler, _textureView, ImageLayout.ShaderReadOnlyOptimal);
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet, DstSet = _descriptorSet, DstBinding = 0, DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &imageInfo,
        };
        _vk.UpdateDescriptorSets(_device, 1, &write, 0, null);
    }

    void DestroyTexture()
    {
        if (_textureView.Handle != 0) _vk.DestroyImageView(_device, _textureView, null);
        if (_textureImage.Handle != 0) _vk.DestroyImage(_device, _textureImage, null);
        if (_textureMemory.Handle != 0) _vk.FreeMemory(_device, _textureMemory, null);
        (_textureView, _textureImage, _textureMemory) = (default, default, default);
    }

    ShaderModule CreateShaderModule(byte[] spirv)
    {
        fixed (byte* code = spirv)
        {
            var info = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)spirv.Length, PCode = (uint*)code };
            Check(_vk.CreateShaderModule(_device, &info, null, out var module), "shader module");
            return module;
        }
    }

    void CreateTargets(int width, int height)
    {
        _vk.DeviceWaitIdle(_device);
        DestroyTargets();
        (_colorImage, _colorMemory, _colorView) = CreateImage(width, height, ColorFormat,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit, ImageAspectFlags.ColorBit);
        (_depthImage, _depthMemory, _depthView) = CreateImage(width, height, DepthFormat,
            ImageUsageFlags.DepthStencilAttachmentBit, ImageAspectFlags.DepthBit);
        var views = stackalloc ImageView[2] { _colorView, _depthView };
        var info = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = _renderPass,
            AttachmentCount = 2,
            PAttachments = views,
            Width = (uint)width,
            Height = (uint)height,
            Layers = 1,
        };
        Check(_vk.CreateFramebuffer(_device, &info, null, out _framebuffer), "framebuffer");
        (_readback, _readbackMemory) = CreateBuffer((ulong)(width * height * 4), BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        (_width, _height) = (width, height);
    }

    (Image, DeviceMemory, ImageView) CreateImage(int width, int height, Format format, ImageUsageFlags usage, ImageAspectFlags aspect)
    {
        var info = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D((uint)width, (uint)height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        Check(_vk.CreateImage(_device, &info, null, out var image), "image");
        _vk.GetImageMemoryRequirements(_device, image, out var requirements);
        var memory = Allocate(requirements, MemoryPropertyFlags.DeviceLocalBit);
        Check(_vk.BindImageMemory(_device, image, memory, 0), "bind image memory");
        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange(aspect, 0, 1, 0, 1),
        };
        Check(_vk.CreateImageView(_device, &viewInfo, null, out var view), "image view");
        return (image, memory, view);
    }

    (Buffer, DeviceMemory) CreateHostBuffer(ReadOnlySpan<byte> data, BufferUsageFlags usage)
    {
        var (buffer, memory) = CreateBuffer((ulong)Math.Max(data.Length, 4), usage, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        void* mapped;
        Check(_vk.MapMemory(_device, memory, 0, (ulong)data.Length, 0, &mapped), "map buffer");
        data.CopyTo(new Span<byte>(mapped, data.Length));
        _vk.UnmapMemory(_device, memory);
        return (buffer, memory);
    }

    (Buffer, DeviceMemory) CreateBuffer(ulong size, BufferUsageFlags usage, MemoryPropertyFlags properties)
    {
        var info = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = size, Usage = usage, SharingMode = SharingMode.Exclusive };
        Check(_vk.CreateBuffer(_device, &info, null, out var buffer), "buffer");
        _vk.GetBufferMemoryRequirements(_device, buffer, out var requirements);
        var memory = Allocate(requirements, properties);
        Check(_vk.BindBufferMemory(_device, buffer, memory, 0), "bind buffer memory");
        return (buffer, memory);
    }

    DeviceMemory Allocate(MemoryRequirements requirements, MemoryPropertyFlags properties)
    {
        _vk.GetPhysicalDeviceMemoryProperties(_physicalDevice, out var memoryProps);
        var type = -1;
        for (var i = 0; i < memoryProps.MemoryTypeCount; i++)
        {
            if ((requirements.MemoryTypeBits & (1u << i)) != 0 && memoryProps.MemoryTypes[i].PropertyFlags.HasFlag(properties))
            {
                type = i;
                break;
            }
        }
        if (type < 0) throw new InvalidOperationException($"No GPU memory type with {properties}.");
        var info = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = requirements.Size, MemoryTypeIndex = (uint)type };
        Check(_vk.AllocateMemory(_device, &info, null, out var memory), "allocate GPU memory");
        return memory;
    }

    // ---- teardown ----------------------------------------------------------------------------------------------

    void DestroyTargets()
    {
        if (_framebuffer.Handle != 0) _vk.DestroyFramebuffer(_device, _framebuffer, null);
        if (_colorView.Handle != 0) _vk.DestroyImageView(_device, _colorView, null);
        if (_depthView.Handle != 0) _vk.DestroyImageView(_device, _depthView, null);
        if (_colorImage.Handle != 0) _vk.DestroyImage(_device, _colorImage, null);
        if (_depthImage.Handle != 0) _vk.DestroyImage(_device, _depthImage, null);
        if (_colorMemory.Handle != 0) _vk.FreeMemory(_device, _colorMemory, null);
        if (_depthMemory.Handle != 0) _vk.FreeMemory(_device, _depthMemory, null);
        if (_readback.Handle != 0) _vk.DestroyBuffer(_device, _readback, null);
        if (_readbackMemory.Handle != 0) _vk.FreeMemory(_device, _readbackMemory, null);
        (_framebuffer, _colorView, _depthView, _colorImage, _depthImage) = (default, default, default, default, default);
        (_colorMemory, _depthMemory, _readback, _readbackMemory) = (default, default, default, default);
        (_width, _height) = (0, 0);
    }

    void DestroyMeshBuffers()
    {
        if (_vertexBuffer.Handle != 0) _vk.DestroyBuffer(_device, _vertexBuffer, null);
        if (_indexBuffer.Handle != 0) _vk.DestroyBuffer(_device, _indexBuffer, null);
        if (_vertexMemory.Handle != 0) _vk.FreeMemory(_device, _vertexMemory, null);
        if (_indexMemory.Handle != 0) _vk.FreeMemory(_device, _indexMemory, null);
        (_vertexBuffer, _indexBuffer, _vertexMemory, _indexMemory, _indexCount, _partIndexCount) = (default, default, default, default, 0, 0);
    }

    public void Dispose()
    {
        _vk.DeviceWaitIdle(_device);
        DestroyMeshBuffers();
        DestroyTargets();
        DestroyTexture();
        _vk.DestroySampler(_device, _sampler, null);
        _vk.DestroyDescriptorPool(_device, _descriptorPool, null);
        _vk.DestroyDescriptorSetLayout(_device, _descriptorLayout, null);
        _vk.DestroyPipeline(_device, _pipeline, null);
        _vk.DestroyPipelineLayout(_device, _pipelineLayout, null);
        _vk.DestroyRenderPass(_device, _renderPass, null);
        _vk.DestroyFence(_device, _fence, null);
        _vk.DestroyCommandPool(_device, _commandPool, null);
        _vk.DestroyDevice(_device, null);
        _vk.DestroyInstance(_instance, null);
        _vk.Dispose();
    }

    static void Check(Result result, string what)
    {
        if (result != Result.Success) throw new InvalidOperationException($"Vulkan: {what} failed ({result}).");
    }
}
