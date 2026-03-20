using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public class RenderMetaballsScreenSpace : ScriptableRendererFeature
{
    class RenderMetaballsDepthPass : ScriptableRenderPass
    {
        public Material WriteDepthMaterial;
        public TextureHandle DepthTextureHandle { get; private set; }

        RenderQueueType _renderQueueType;
        FilteringSettings _filteringSettings;
        List<ShaderTagId> _shaderTagIdList = new List<ShaderTagId>();

        private class PassData { public RendererListHandle rendererList; }

        public RenderMetaballsDepthPass(RenderPassEvent renderPassEvent, string[] shaderTags, RenderQueueType renderQueueType, int layerMask)
        {
            this.renderPassEvent = renderPassEvent;
            this._renderQueueType = renderQueueType;
            RenderQueueRange renderQueueRange = (renderQueueType == RenderQueueType.Transparent) ? RenderQueueRange.transparent : RenderQueueRange.opaque;
            _filteringSettings = new FilteringSettings(renderQueueRange, layerMask);

            if (shaderTags != null && shaderTags.Length > 0)
            {
                foreach (var passName in shaderTags)
                    _shaderTagIdList.Add(new ShaderTagId(passName));
            }
            else
            {
                _shaderTagIdList.Add(new ShaderTagId("SRPDefaultUnlit"));
                _shaderTagIdList.Add(new ShaderTagId("UniversalForward"));
                _shaderTagIdList.Add(new ShaderTagId("UniversalForwardOnly"));
                _shaderTagIdList.Add(new ShaderTagId("LightweightForward"));
            }
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();

            TextureDesc desc = new TextureDesc(cameraData.cameraTargetDescriptor.width, cameraData.cameraTargetDescriptor.height);
            desc.colorFormat = cameraData.cameraTargetDescriptor.graphicsFormat;
            desc.depthBufferBits = DepthBits.None;
            desc.msaaSamples = MSAASamples.None; // Force NO MSAA pour éviter les crashs
            desc.name = "_MetaballDepthRT";
            DepthTextureHandle = renderGraph.CreateTexture(desc);

            TextureDesc depthDesc = new TextureDesc(cameraData.cameraTargetDescriptor.width, cameraData.cameraTargetDescriptor.height);
            depthDesc.colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.None;
            depthDesc.depthBufferBits = DepthBits.Depth32;
            depthDesc.msaaSamples = MSAASamples.None;
            depthDesc.name = "_MetaballDepthRT_ZBuffer";
            TextureHandle zBuffer = renderGraph.CreateTexture(depthDesc);

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Metaballs Depth Pass", out var passData))
            {
                builder.SetRenderAttachment(DepthTextureHandle, 0, AccessFlags.Write);
                builder.SetRenderAttachmentDepth(zBuffer, AccessFlags.Write);

                SortingCriteria sortingCriteria = (_renderQueueType == RenderQueueType.Transparent) ? SortingCriteria.CommonTransparent : cameraData.defaultOpaqueSortFlags;
                DrawingSettings drawingSettings = new DrawingSettings(_shaderTagIdList[0], new SortingSettings(cameraData.camera) { criteria = sortingCriteria })
                {
                    overrideMaterial = WriteDepthMaterial
                };
                for (int i = 1; i < _shaderTagIdList.Count; i++)
                    drawingSettings.SetShaderPassName(i, _shaderTagIdList[i]);

                RendererListParams listParams = new RendererListParams(renderingData.cullResults, drawingSettings, _filteringSettings);
                passData.rendererList = renderGraph.CreateRendererList(listParams);
                builder.UseRendererList(passData.rendererList);

                builder.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    RasterCommandBuffer cmd = context.cmd;
                    cmd.ClearRenderTarget(RTClearFlags.All, Color.clear, 1, 0);
                    cmd.DrawRendererList(data.rendererList);
                });
            }
        }
    }

    class RenderMetaballsScreenSpacePass : ScriptableRenderPass
    {
        public Material BlitMaterial;
        public int BlurPasses;
        public float BlurDistance;
        public RenderMetaballsDepthPass DepthPass;

        Material _blurMaterial;
        Material _blitCopyDepthMaterial;
        RenderQueueType _renderQueueType;
        FilteringSettings _filteringSettings;
        List<ShaderTagId> _shaderTagIdList = new List<ShaderTagId>();

        private class DrawPassData { public RendererListHandle rendererList; public TextureHandle cameraDepth; public Material copyDepthMat; }
        private class BlurPassData { public TextureHandle sourceTex; public TextureHandle depthTex; public Material blurMat; public float offset; public float blurDistance; }
        private class BlitPassData { public TextureHandle sourceTex; public Material blitMat; }

        public RenderMetaballsScreenSpacePass(RenderPassEvent renderPassEvent, string[] shaderTags, RenderQueueType renderQueueType, int layerMask)
        {
            this.renderPassEvent = renderPassEvent;
            this._renderQueueType = renderQueueType;
            RenderQueueRange renderQueueRange = (renderQueueType == RenderQueueType.Transparent) ? RenderQueueRange.transparent : RenderQueueRange.opaque;
            _filteringSettings = new FilteringSettings(renderQueueRange, layerMask);

            if (shaderTags != null && shaderTags.Length > 0)
            {
                foreach (var passName in shaderTags)
                    _shaderTagIdList.Add(new ShaderTagId(passName));
            }
            else
            {
                _shaderTagIdList.Add(new ShaderTagId("SRPDefaultUnlit"));
                _shaderTagIdList.Add(new ShaderTagId("UniversalForward"));
                _shaderTagIdList.Add(new ShaderTagId("UniversalForwardOnly"));
                _shaderTagIdList.Add(new ShaderTagId("LightweightForward"));
            }

            _blitCopyDepthMaterial = new Material(Shader.Find("Hidden/BlitToDepth"));
            _blurMaterial = new Material(Shader.Find("Hidden/KawaseBlur"));
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();

            TextureHandle cameraColor = resourceData.activeColorTexture;
            TextureHandle cameraDepth = resourceData.activeDepthTexture;
            TextureHandle metaballDepthRT = DepthPass.DepthTextureHandle;

            TextureDesc colorDesc = new TextureDesc(cameraData.cameraTargetDescriptor.width, cameraData.cameraTargetDescriptor.height);
            colorDesc.colorFormat = cameraData.cameraTargetDescriptor.graphicsFormat;
            colorDesc.depthBufferBits = DepthBits.None;
            colorDesc.msaaSamples = MSAASamples.None; // Force NO MSAA
            colorDesc.filterMode = FilterMode.Bilinear;

            colorDesc.name = "_MetaballRT";
            TextureHandle metaballRT = renderGraph.CreateTexture(colorDesc);

            colorDesc.name = "_MetaballRT2";
            TextureHandle metaballRT2 = renderGraph.CreateTexture(colorDesc);

            TextureDesc depthDesc = new TextureDesc(cameraData.cameraTargetDescriptor.width, cameraData.cameraTargetDescriptor.height);
            depthDesc.colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.None;
            depthDesc.depthBufferBits = DepthBits.Depth32;
            depthDesc.msaaSamples = MSAASamples.None; // Force NO MSAA
            depthDesc.name = "_MetaballCustomDepth";
            TextureHandle customDepthRT = renderGraph.CreateTexture(depthDesc);

            using (var builder = renderGraph.AddRasterRenderPass<DrawPassData>("Metaballs Draw Setup", out var passData))
            {
                builder.SetRenderAttachment(metaballRT, 0, AccessFlags.Write);
                builder.SetRenderAttachmentDepth(customDepthRT, AccessFlags.Write);

                builder.UseTexture(cameraDepth, AccessFlags.Read);
                passData.cameraDepth = cameraDepth;
                passData.copyDepthMat = _blitCopyDepthMaterial;

                SortingCriteria sortingCriteria = (_renderQueueType == RenderQueueType.Transparent) ? SortingCriteria.CommonTransparent : cameraData.defaultOpaqueSortFlags;
                DrawingSettings drawingSettings = new DrawingSettings(_shaderTagIdList[0], new SortingSettings(cameraData.camera) { criteria = sortingCriteria });
                for (int i = 1; i < _shaderTagIdList.Count; i++) drawingSettings.SetShaderPassName(i, _shaderTagIdList[i]);

                RendererListParams listParams = new RendererListParams(renderingData.cullResults, drawingSettings, _filteringSettings);
                passData.rendererList = renderGraph.CreateRendererList(listParams);
                builder.UseRendererList(passData.rendererList);

                builder.SetRenderFunc((DrawPassData data, RasterGraphContext context) =>
                {
                    RasterCommandBuffer cmd = context.cmd;
                    cmd.ClearRenderTarget(RTClearFlags.All, Color.clear, 1, 0);
                    Blitter.BlitTexture(cmd, data.cameraDepth, new Vector4(1, 1, 0, 0), data.copyDepthMat, 0);
                    cmd.DrawRendererList(data.rendererList);
                });
            }

            TextureHandle currentSource = metaballRT;
            TextureHandle currentDest = metaballRT2;
            float offset = 1.5f;

            for (int i = 0; i < BlurPasses; i++)
            {
                using (var builder = renderGraph.AddRasterRenderPass<BlurPassData>($"Metaballs Blur Pass {i}", out var passData))
                {
                    builder.SetRenderAttachment(currentDest, 0, AccessFlags.Write);

                    builder.UseTexture(currentSource, AccessFlags.Read);
                    passData.sourceTex = currentSource;

                    builder.UseTexture(metaballDepthRT, AccessFlags.Read);
                    passData.depthTex = metaballDepthRT;

                    passData.blurMat = _blurMaterial;
                    passData.offset = offset;
                    passData.blurDistance = BlurDistance;

                    builder.SetRenderFunc((BlurPassData data, RasterGraphContext context) =>
                    {
                        RasterCommandBuffer cmd = context.cmd;
                        data.blurMat.SetTexture("_BlurDepthTex", data.depthTex);
                        data.blurMat.SetFloat("_BlurDistance", data.blurDistance);
                        data.blurMat.SetFloat("_Offset", data.offset);

                        Blitter.BlitTexture(cmd, data.sourceTex, new Vector4(1, 1, 0, 0), data.blurMat, 0);
                    });
                }

                offset += (i == 0) ? 0f : 1.0f;
                TextureHandle tmp = currentSource;
                currentSource = currentDest;
                currentDest = tmp;
            }

            using (var builder = renderGraph.AddRasterRenderPass<BlitPassData>("Metaballs Final Blit", out var passData))
            {
                builder.SetRenderAttachment(cameraColor, 0, AccessFlags.Write);

                builder.UseTexture(currentSource, AccessFlags.Read);
                passData.sourceTex = currentSource;

                passData.blitMat = BlitMaterial;

                builder.SetRenderFunc((BlitPassData data, RasterGraphContext context) =>
                {
                    Blitter.BlitTexture(context.cmd, data.sourceTex, new Vector4(1, 1, 0, 0), data.blitMat, 0);
                });
            }
        }
    }

    public string PassTag = "RenderMetaballsScreenSpace";
    public RenderPassEvent Event = RenderPassEvent.AfterRenderingOpaques;

    // Cette classe est déclarée dans RenderMetaballs.cs pour tout le projet !
    public CustomFilterSettings FilterSettings = new CustomFilterSettings();

    public Material BlitMaterial;
    public Material WriteDepthMaterial;

    RenderMetaballsDepthPass _renderMetaballsDepthPass;
    RenderMetaballsScreenSpacePass _scriptableMetaballsScreenSpacePass;

    [Range(1, 15)] public int BlurPasses = 1;
    [Range(0f, 1f)] public float BlurDistance = 0.5f;

    public override void Create()
    {
        _renderMetaballsDepthPass = new RenderMetaballsDepthPass(Event, FilterSettings.PassNames, FilterSettings.RenderQueueType, FilterSettings.LayerMask)
        {
            WriteDepthMaterial = WriteDepthMaterial
        };

        _scriptableMetaballsScreenSpacePass = new RenderMetaballsScreenSpacePass(Event, FilterSettings.PassNames, FilterSettings.RenderQueueType, FilterSettings.LayerMask)
        {
            BlitMaterial = BlitMaterial,
            BlurPasses = BlurPasses,
            BlurDistance = BlurDistance,
            DepthPass = _renderMetaballsDepthPass
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        renderer.EnqueuePass(_renderMetaballsDepthPass);
        renderer.EnqueuePass(_scriptableMetaballsScreenSpacePass);
    }
}