using Microsoft.AspNetCore.Components.Web;
using Microsoft.FluentUI.AspNetCore.Components.Utilities;

namespace DotNetLab.Controls
{
    public partial class SettingsButton : SettingsCardBase
    {
        protected override string? ClassValue => new CssBuilder(Class)
            .AddClass("settings-button")
            .Build();

        /// <summary>
        /// Gets or sets the icon that is shown when IsClickEnabled is set to true.
        /// </summary>
        [Parameter]
        public RenderFragment? ActionIcon { get; set; }

        /// <summary>
        /// Gets or sets the tooltip of the ActionIcon.
        /// </summary>
        [Parameter]
        public string ActionIconToolTip { get; set; } = "More";

        /// <summary>
        /// Command executed when the user clicks on the button.
        /// </summary>
        [Parameter]
        public EventCallback<MouseEventArgs> OnClick { get; set; }

        /// <summary>
        /// Gets or sets the fill color of the button.
        /// </summary>
        [CascadingParameter(Name = nameof(FillColor))]
        private string FillColor { get; set; } = "neutralFillInputRest";
    }
}
