namespace MapRenderer.Unity.Rendering
{
    /// <summary>
    /// Lit lighting workflow. Values match the <c>_WorkflowMode</c> material float (0 = Specular,
    /// 1 = Metallic), so the enum maps 1:1 onto the property and its names generate the dropdown labels.
    /// Lives in the runtime assembly (shared by the tweakers and the editor ShaderGUI). (S58)
    /// </summary>
    public enum WorkflowMode
    {
        Specular = 0,
        Metallic = 1,
    }
}