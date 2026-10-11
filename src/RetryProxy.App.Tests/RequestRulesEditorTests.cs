using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using RetryProxy.Core.Config;
using RetryProxy.View.Drawers;
using Xunit;

namespace RetryProxy.App.Tests;

public sealed class RequestRulesEditorTests
{
    private static void Click(object button) => ((ButtonBase)button).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static RequestRule Rule(string path, string json) => new() { Path = path, ValueJson = json };

    [Fact]
    public void EditorKeepsDraftsWhenReorderingAndRejectsInvalidValues() => Run(() =>
    {
        var editor = new RequestRulesEditor();
        editor.Load(ClientType.Codex, new[] { Rule("temperature", "1"), Rule("reasoning.effort", "\"high\"") });
        var rows = (StackPanel)editor.FindName("Rows");
        var first = (RequestRuleField)rows.Children[0];
        ((TextBox)first.FindName("ValueBox")).Text = "invalid";
        Click(first.FindName("DownButton"));
        Assert.Same(first, rows.Children[1]);
        Assert.StartsWith("请求规则 2：", editor.Read(out _));
        ((TextBox)first.FindName("ValueBox")).Text = "0.5";
        Assert.Null(editor.Read(out var rules));
        Assert.Equal(new[] { "reasoning.effort", "temperature" }, rules.Select(rule => rule.Path));
        Assert.Equal("0.5", rules[1].ValueJson);
        ((ComboBox)first.FindName("ActionBox")).SelectedIndex = 2;
        Assert.Equal(Visibility.Collapsed, ((StackPanel)first.FindName("ValuePanel")).Visibility);
        Assert.Null(editor.Read(out rules));
        Assert.Equal(RequestRuleAction.Remove, rules[1].Action);
    });

    [Fact]
    public void TextAndBooleanValuesRoundTripWithoutRequiringJsonSyntax() => Run(() =>
    {
        var field = new RequestRuleField(ClientType.Codex, Rule("custom", "\"line\\nquoted\\\"\""));
        Assert.Equal("line\nquoted\"", ((TextBox)field.FindName("ValueBox")).Text);
        Assert.Null(field.Read(out var text));
        Assert.Equal("line\nquoted\"", System.Text.Json.JsonSerializer.Deserialize<string>(text.ValueJson));
        ((ComboBox)field.FindName("ValueTypeBox")).SelectedIndex = 2;
        ((ComboBox)field.FindName("BooleanBox")).SelectedIndex = 1;
        Assert.Null(field.Read(out var boolean));
        Assert.Equal("false", boolean.ValueJson);
        ((ComboBox)field.FindName("ParameterBox")).SelectedIndex = 1;
        Assert.Null(field.Read(out var preset));
        Assert.Equal("temperature", preset.Path);
    });

    [Fact]
    public void ProviderDraftTracksRuleChangesWithoutMutatingTheOriginal() => Run(() =>
    {
        var source = new ProviderEndpoint
        {
            Id = "p", Name = "p", BaseUrl = "https://fixture.example", RequestRules = new() { Rule("temperature", "0.5") },
            Keys = new List<ProviderKey> { new() { Id = "k", Name = "k", ApiKey = "sk-fixture" } },
        };
        using var drawer = new ProviderDrawer(source, false);
        Assert.False(drawer.HasChanges);
        var editor = (RequestRulesEditor)drawer.FindName("RulesEditor");
        var field = (RequestRuleField)((StackPanel)editor.FindName("Rows")).Children[0];
        ((CheckBox)field.FindName("EnabledCheck")).IsChecked = false;
        Assert.True(drawer.HasChanges);
        Assert.Null(drawer.ReadDraft());
        Assert.False(drawer.Draft.RequestRules[0].Enabled);
        Assert.True(source.RequestRules[0].Enabled);
    });

    private static void Run(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { test(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
