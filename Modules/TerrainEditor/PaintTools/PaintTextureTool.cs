// Unity C# reference source
// Copyright (c) Unity Technologies. For terms of use, see
// https://unity3d.com/legal/licenses/Unity_Reference_Only_License

using UnityEngine;
using UnityEditor;
using System;
using UnityEngine.Experimental.TerrainAPI;
using UnityEditor.ShortcutManagement;

namespace UnityEditor.Experimental.TerrainAPI
{
    [FilePathAttribute("Library/TerrainTools/PaintTexture", FilePathAttribute.Location.ProjectFolder)]
    internal class PaintTextureTool : TerrainPaintTool<PaintTextureTool>
    {
        const string toolName = "Paint Texture";

        Editor m_TemplateMaterialEditor = null;
        Editor m_SelectedTerrainLayerInspector = null;

        [SerializeField]
        TerrainLayer m_SelectedTerrainLayer = null;

        [FormerlyPrefKeyAs("Terrain/Texture Paint", "f4")]
        [Shortcut("Terrain/Paint Texture", typeof(TerrainToolShortcutContext))]
        static void SelectShortcut(ShortcutArguments args)
        {
            TerrainToolShortcutContext context = (TerrainToolShortcutContext)args.context;
            context.SelectPaintTool<PaintTextureTool>();
        }

        public override string GetName()
        {
            return toolName;
        }

        public override string GetDesc()
        {
            return "Paints the selected material layer onto the terrain texture";
        }

        public override bool OnPaint(Terrain terrain, IOnPaint editContext)
        {
            BrushTransform brushXform = TerrainPaintUtility.CalculateBrushTransform(terrain, editContext.uv, editContext.brushSize, 0.0f);
            PaintContext paintContext = TerrainPaintUtility.BeginPaintTexture(terrain, brushXform.GetBrushXYBounds(), m_SelectedTerrainLayer);
            if (paintContext == null)
                return false;

            Material mat = TerrainPaintUtility.GetBuiltinPaintMaterial();

            // apply brush
            float targetAlpha = 1.0f;       // always 1.0 now -- no subtractive painting (we assume this in the ScatterAlphaMap)
            Vector4 brushParams = new Vector4(editContext.brushStrength, targetAlpha, 0.0f, 0.0f);
            mat.SetTexture("_BrushTex", editContext.brushTexture);
            mat.SetVector("_BrushParams", brushParams);

            TerrainPaintUtility.SetupTerrainToolMaterialProperties(paintContext, brushXform, mat);

            Graphics.Blit(paintContext.sourceRenderTexture, paintContext.destinationRenderTexture, mat, (int)TerrainPaintUtility.BuiltinPaintMaterialPasses.PaintTexture);

            TerrainPaintUtility.EndPaintTexture(paintContext, "Terrain Paint - Texture");
            return true;
        }

        public override void OnSceneGUI(Terrain terrain, IOnSceneGUI editContext)
        {
            // We're only doing painting operations, early out if it's not a repaint
            if (Event.current.type != EventType.Repaint)
                return;

            if (editContext.hitValidTerrain)
            {
                BrushTransform brushXform = TerrainPaintUtility.CalculateBrushTransform(terrain, editContext.raycastHit.textureCoord, editContext.brushSize, 0.0f);
                PaintContext ctx = TerrainPaintUtility.BeginPaintHeightmap(terrain, brushXform.GetBrushXYBounds(), 1);
                TerrainPaintUtilityEditor.DrawBrushPreview(ctx, TerrainPaintUtilityEditor.BrushPreview.SourceRenderTexture, editContext.brushTexture, brushXform, TerrainPaintUtilityEditor.GetDefaultBrushPreviewMaterial(), 0);
                TerrainPaintUtility.ReleaseContextResources(ctx);
            }
        }

        private const int kTemplateMaterialEditorControl = 67890;
        private const int kSelectedTerrainLayerEditorControl = 67891;

        public override void OnInspectorGUI(Terrain terrain, IOnInspectorGUI editContext)
        {
            GUILayout.Label("Settings", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();

            EditorGUILayout.Space();
            Editor.DrawFoldoutInspector(terrain.materialTemplate, ref m_TemplateMaterialEditor);

            EditorGUILayout.Space();

            int layerIndex = TerrainPaintUtility.FindTerrainLayerIndex(terrain, m_SelectedTerrainLayer);
            layerIndex = TerrainLayerUtility.ShowTerrainLayersSelectionHelper(terrain, layerIndex);
            EditorGUILayout.Space();

            if (EditorGUI.EndChangeCheck())
            {
                m_SelectedTerrainLayer = layerIndex != -1 ? terrain.terrainData.terrainLayers[layerIndex] : null;
                Save(true);
            }

            terrain.materialTemplate.SetFloat("_NumLayersCount", terrain.terrainData.terrainLayers.Length);
            TerrainLayerUtility.ShowTerrainLayerGUI(terrain, m_SelectedTerrainLayer, ref m_SelectedTerrainLayerInspector,
                (m_TemplateMaterialEditor as MaterialEditor)?.customShaderGUI as ITerrainLayerCustomUI);
            EditorGUILayout.Space();

            editContext.ShowBrushesGUI(5);
        }
    }
}
