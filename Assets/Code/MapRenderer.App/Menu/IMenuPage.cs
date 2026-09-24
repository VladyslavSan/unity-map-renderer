namespace MapRenderer.App.Menu
{
    /// <summary>
    /// One page of the <see cref="MenuOverlay"/> — a self-contained IMGUI screen with its own layout.
    /// The host owns a navigation stack of these; a page opens a sub-page with <see cref="MenuOverlay.Push"/>
    /// and returns via the host's Back button (<see cref="MenuOverlay.Pop"/>).
    /// Non-local invariant: <see cref="Draw"/> runs in both the Layout and Repaint events, so it emits the same
    /// controls in both; a click's state change takes effect next frame (draw a placeholder until then).
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
