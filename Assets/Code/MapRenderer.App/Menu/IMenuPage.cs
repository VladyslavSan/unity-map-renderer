namespace MapRenderer.App.Menu
{
    /// <summary>
    /// One page of the <see cref="MenuOverlay"/> — a self-contained IMGUI screen with its own layout.
    /// The host owns a navigation stack of these; a page opens a sub-page with <see cref="MenuOverlay.Push"/>
    /// and returns via the host's Back button (<see cref="MenuOverlay.Pop"/>).
    ///
    /// <para><b>IMGUI contract:</b> <see cref="Draw"/> runs inside the host's window during both the Layout and
    /// Repaint events, so it must emit the SAME set of controls in both — never make a control's existence depend
    /// on a click processed this frame (draw a placeholder instead). State changes triggered by a click take
    /// effect on the next frame, which is normal for IMGUI.</para>
    /// </summary>
    internal interface IMenuPage
    {
        /// <summary>Title shown in the menu window's title bar while this page is on top of the stack.</summary>
        string Title { get; }

        /// <summary>Draw this page's controls. <paramref name="menu"/> is the host — read
        /// <see cref="MenuOverlay.MapComponent"/> off it and push sub-pages through it.</summary>
        /// <param name="menu">The owning overlay.</param>
        void Draw(MenuOverlay menu);
    }
}
