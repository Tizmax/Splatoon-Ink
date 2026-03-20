using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

[System.Serializable]
public class CustomRenderObjectsSettings
{
    public string passTag = "RenderMetaballs";
    public RenderPassEvent Event = RenderPassEvent.AfterRenderingOpaques;
    public CustomFilterSettings filterSettings = new CustomFilterSettings();
    public CustomCameraSettings cameraSettings = new CustomCameraSettings();
}

[System.Serializable]
public class CustomFilterSettings
{
    public RenderQueueType RenderQueueType = RenderQueueType.Opaque;
    public LayerMask LayerMask = -1;
    public string[] PassNames;
}

[System.Serializable]
public class CustomCameraSettings
{
    public bool overrideCamera = false;
    public bool restoreCamera = true;
}

public class RenderMetaballs : ScriptableRendererFeature
{
    class RenderMetaballsPass : ScriptableRenderPass
    {
        public Material BlitMaterial;
        public Material BlurMaterial;
        public Material BlitCopyDepthMaterial;

        int _downsamplingAmount = 4;
        RenderQueueType renderQueueType;
        FilteringSettings m_FilteringSettings;
        CustomCameraSettings m_CameraSettings;

        List<ShaderTagId> m_ShaderTagIdList = new List<ShaderTagId>();

        private class DrawPassData { public RendererListHandle rendererList; public TextureHandle cameraDepth; public Material copyDepthMat; }
        private class BlitPassData { public TextureHandle sourceTex; public Material blitMat; }
        private class BlurPassData { public TextureHandle sourceTex; public Material blurMat; }

        public RenderMetaballsPass(string profilerTag, RenderPassEvent renderPassEvent, string[] shaderTags,
            RenderQueueType renderQueueType, int layerMask, CustomCameraSettings cameraSettings, int downsamplingAmount)
        {
            this.renderPassEvent = renderPassEvent;
            this.renderQueueType = renderQueueType;

            RenderQueueRange renderQueueRange = (renderQueueType == RenderQueueType.Transparent) ? RenderQueueRange.transparent : RenderQueueRange.opaque;
            m_FilteringSettings = new FilteringSettings(renderQueueRange, layerMask);

            if (shaderTags != null && shaderTags.Length > 0)
            {
                foreach (var passName in shaderTags)
                    m_ShaderTagIdList.Add(new ShaderTagId(passName));
            }
            else
            {
                m_ShaderTagIdList.Add(new ShaderTagId("SRPDefaultUnlit"));
                m_ShaderTagIdList.Add(new ShaderTagId("UniversalForward"));
                m_ShaderTagIdList.Add(new ShaderTagId("UniversalForwardOnly"));
                m_ShaderTagIdList.Add(new ShaderTagId("LightweightForward"));
            }

            m_CameraSettings = cameraSettings;
            _downsamplingAmount = downsamplingAmount;

            BlitCopyDepthMaterial = new Material(Shader.Find("Hidden/BlitToDepth"));
            BlurMaterial = new Material(Shader.Find("Hidden/KawaseBlur"));
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();

            TextureHandle cameraColor = resourceData.activeColorTexture;
            TextureHandle cameraDepth = resourceData.activeDepthTexture;

            // 1. Texture de Couleur (Basse résolution, Sans Profondeur, Sans MSAA)
            TextureDesc smallDesc = new TextureDesc(cameraData.cameraTargetDescriptor.width / _downsamplingAmount, cameraData.cameraTargetDescriptor.height / _downsamplingAmount);
            smallDesc.colorFormat = cameraData.cameraTargetDescriptor.graphicsFormat;
            smallDesc.depthBufferBits = DepthBits.None;
            smallDesc.filterMode = FilterMode.Bilinear;
            smallDesc.name = "_MetaballRTSmall";
            TextureHandle metaballRTSmall = renderGraph.CreateTexture(smallDesc);

            // 2. Texture de Profondeur Personnalisée (Basse résolution, Sans Couleur, Sans MSAA)
            TextureDesc depthDesc = new TextureDesc(cameraData.cameraTargetDescriptor.width / _downsamplingAmount, cameraData.cameraTargetDescriptor.height / _downsamplingAmount);
            depthDesc.colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.None; // Aucune couleur nécessaire
            depthDesc.depthBufferBits = DepthBits.Depth32;
            depthDesc.name = "_MetaballCustomDepthSmall";
            TextureHandle customDepthRTSmall = renderGraph.CreateTexture(depthDesc);

            // 3. Textures Large pour le traitement d'image
            TextureDesc largeDesc = new TextureDesc(cameraData.cameraTargetDescriptor.width, cameraData.cameraTargetDescriptor.height);
            largeDesc.colorFormat = cameraData.cameraTargetDescriptor.graphicsFormat;
            largeDesc.depthBufferBits = 0;
            largeDesc.filterMode = FilterMode.Bilinear;
            largeDesc.name = "_MetaballRTLarge";
            TextureHandle metaballRTLarge = renderGraph.CreateTexture(largeDesc);

            largeDesc.name = "_MetaballRTLarge2";
            TextureHandle metaballRTLarge2 = renderGraph.CreateTexture(largeDesc);

            // --- Passe 1 : Dessin avec Profondeur Downsamplée ---
            using (var builder = renderGraph.AddRasterRenderPass<DrawPassData>("Metaballs Draw Small", out var passData))
            {
                builder.SetRenderAttachment(metaballRTSmall, 0, AccessFlags.Write);
                builder.SetRenderAttachmentDepth(customDepthRTSmall, AccessFlags.Write);

                builder.UseTexture(cameraDepth, AccessFlags.Read);
                passData.cameraDepth = cameraDepth;
                passData.copyDepthMat = BlitCopyDepthMaterial;

                SortingCriteria sortingCriteria = (renderQueueType == RenderQueueType.Transparent) ? SortingCriteria.CommonTransparent : cameraData.defaultOpaqueSortFlags;
                DrawingSettings drawingSettings = new DrawingSettings(m_ShaderTagIdList[0], new SortingSettings(cameraData.camera) { criteria = sortingCriteria });
                for (int i = 1; i < m_ShaderTagIdList.Count; i++) drawingSettings.SetShaderPassName(i, m_ShaderTagIdList[i]);

                RendererListParams listParams = new RendererListParams(renderingData.cullResults, drawingSettings, m_FilteringSettings);
                passData.rendererList = renderGraph.CreateRendererList(listParams);
                builder.UseRendererList(passData.rendererList);

                builder.SetRenderFunc((DrawPassData data, RasterGraphContext context) =>
                {
                    RasterCommandBuffer cmd = context.cmd;
                    cmd.ClearRenderTarget(RTClearFlags.All, Color.clear, 1, 0);

                    // Copie manuelle de la profondeur de la scène vers notre texture personnalisée
                    Blitter.BlitTexture(cmd, data.cameraDepth, new Vector4(1, 1, 0, 0), data.copyDepthMat, 0);

                    // Dessin des objets de metaballs avec le Z-Test réactivé
                    cmd.DrawRendererList(data.rendererList);
                });
            }

            // --- Passe 2 : Upscale vers la grande texture ---
            using (var builder = renderGraph.AddRasterRenderPass<BlitPassData>("Metaballs Upscale", out var passData))
            {
                builder.SetRenderAttachment(metaballRTLarge, 0, AccessFlags.Write);

                builder.UseTexture(metaballRTSmall, AccessFlags.Read);
                passData.sourceTex = metaballRTSmall;
                passData.blitMat = BlitMaterial;

                builder.SetRenderFunc((BlitPassData data, RasterGraphContext context) =>
                {
                    RasterCommandBuffer cmd = context.cmd;
                    cmd.ClearRenderTarget(RTClearFlags.All, Color.clear, 1, 0);
                    Blitter.BlitTexture(cmd, data.sourceTex, new Vector4(1, 1, 0, 0), data.blitMat, 0);
                });
            }

            // --- Passe 3 : Blur (Flou Kawase) ---
            using (var builder = renderGraph.AddRasterRenderPass<BlurPassData>("Metaballs Blur", out var passData))
            {
                builder.SetRenderAttachment(metaballRTLarge2, 0, AccessFlags.Write);

                builder.UseTexture(metaballRTLarge, AccessFlags.Read);
                passData.sourceTex = metaballRTLarge;
                passData.blurMat = BlurMaterial;

                builder.SetRenderFunc((BlurPassData data, RasterGraphContext context) =>
                {
                    data.blurMat.SetVector("_Offsets", new Vector4(1.5f, 2.0f, 2.5f, 3.0f));
                    Blitter.BlitTexture(context.cmd, data.sourceTex, new Vector4(1, 1, 0, 0), data.blurMat, 0);
                });
            }

            // --- Passe 4 : Final Blit sur l'écran (Caméra) ---
            using (var builder = renderGraph.AddRasterRenderPass<BlitPassData>("Metaballs Final Blit", out var passData))
            {
                builder.SetRenderAttachment(cameraColor, 0, AccessFlags.Write);

                builder.UseTexture(metaballRTLarge2, AccessFlags.Read);
                passData.sourceTex = metaballRTLarge2;
                passData.blitMat = BlitMaterial;

                builder.SetRenderFunc((BlitPassData data, RasterGraphContext context) =>
                {
                    Blitter.BlitTexture(context.cmd, data.sourceTex, new Vector4(1, 1, 0, 0), data.blitMat, 0);
                });
            }
        }
    }

    public Material blitMaterial;
    RenderMetaballsPass _scriptableMetaballsPass;
    public CustomRenderObjectsSettings renderObjectsSettings = new CustomRenderObjectsSettings();
    [Range(1, 16)] public int downsamplingAmount = 4;

    public override void Create()
    {
        CustomFilterSettings filter = renderObjectsSettings.filterSettings;
        _scriptableMetaballsPass = new RenderMetaballsPass(renderObjectsSettings.passTag, renderObjectsSettings.Event,
            filter.PassNames, filter.RenderQueueType, filter.LayerMask, renderObjectsSettings.cameraSettings, downsamplingAmount)
        {
            BlitMaterial = blitMaterial,
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        renderer.EnqueuePass(_scriptableMetaballsPass);
    }
}