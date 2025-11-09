using Microsoft.FluentUI.AspNetCore.Components;

namespace DotNetLab.Controls
{
    public abstract class SettingsCardBase : FluentComponentBase
    {
        protected abstract string? ClassValue { get; }

        /// <summary>
        /// Gets or sets the Header.
        /// </summary>
        [Parameter]
        public RenderFragment? Header { get; set; }

        /// <summary>
        /// Gets or sets the description.
        /// </summary>
        [Parameter]
        public RenderFragment? Description { get; set; }

        /// <summary>
        /// Gets or sets the icon on the left.
        /// </summary>
        [Parameter]
        public RenderFragment? Icon { get; set; }

        /// <summary>
        /// Gets or sets the content of a ContentControl.
        /// </summary>
        [Parameter]
        public RenderFragment? ActionContent { get; set; }
    }
}
