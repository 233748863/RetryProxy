using System.Windows;
using System.Windows.Controls;

namespace RetryProxy.View.Pages;

public partial class ConversationPage : UserControl
{
    public ConversationPage() => InitializeComponent();

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var wide = ActualWidth >= 580;
        Lists.ColumnDefinitions[1].Width = new GridLength(wide ? 1.35 : 0, GridUnitType.Star);
        Grid.SetColumn(RequestCard, wide ? 1 : 0);
        Grid.SetRow(RequestCard, wide ? 0 : 1);
        GroupCard.Margin = wide ? new Thickness(0, 0, 12, 0) : new Thickness(0, 0, 0, 12);
    }
}
