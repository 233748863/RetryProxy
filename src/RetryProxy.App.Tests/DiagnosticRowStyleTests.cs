using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using Xunit;

namespace RetryProxy.App.Tests;

public sealed class DiagnosticRowStyleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RowContainerOnlyPresentsContentWithoutItsOwnHighlight(bool selected)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var source = typeof(DiagnosticRowStyleTests).Assembly
                    .GetManifestResourceStream("RequestDiagnosticsPage.xaml")!;
                var document = XDocument.Load(source);
                XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
                XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
                var element = new XElement(document.Descendants(presentation + "Style")
                    .Single(style => (string?)style.Attribute(xaml + "Key") == "DiagnosticRow"));
                element.Attribute(xaml + "Key")!.Remove();
                var style = (Style)XamlReader.Parse(element.ToString());
                var content = new Border { Height = 38, CornerRadius = new CornerRadius(4) };
                var item = new ListBoxItem { Style = style, Content = content, IsSelected = selected };
                item.Measure(new Size(680, 100));
                item.Arrange(new Rect(0, 0, 680, item.DesiredSize.Height));
                item.ApplyTemplate();
                Assert.False(item.Focusable);
                Assert.Equal(HorizontalAlignment.Stretch, item.HorizontalContentAlignment);
                Assert.Equal(1, VisualTreeHelper.GetChildrenCount(item));
                var presenter = Assert.IsType<ContentPresenter>(VisualTreeHelper.GetChild(item, 0));
                Assert.Same(content, presenter.Content);
                Assert.Equal(680, content.ActualWidth);
                Assert.Empty(item.Template.Triggers.Cast<TriggerBase>());
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Style rendering timed out");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
