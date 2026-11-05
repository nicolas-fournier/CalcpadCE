using System.Windows;

namespace Calcpad.Wpf.Ai
{
    /// <summary>Non-modal tool window hosting the <see cref="AssistantPanel"/>.</summary>
    public partial class AssistantWindow : Window
    {
        public AssistantWindow()
        {
            InitializeComponent();
        }

        /// <summary>The hosted assistant panel, for wiring context and insertion callbacks.</summary>
        public AssistantPanel Assistant => Panel;
    }
}
