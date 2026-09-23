// <copyright file="ClipboardInterceptor.cs" company="RetryProxy">
// Part of the RetryProxy (GPL-3.0) - https://github.com/babalae/better-genshin-impact
// 
// Original Source:
//   MaaAssistantArknights (AGPL-3.0) - https://github.com/MaaAssistantArknights/MaaAssistantArknights
//   Copyright (C) 2021-2025 MaaAssistantArknights Contributors
//
// Author of this file: uye (owner of MaaAssistantArknights)
//
// This file is originally developed in the MaaAssistantArknights project,
// and re-licensed under the GNU General Public License v3.0 (GPL-3.0 only) by the original author,
// for use in the RetryProxy. This license applies to this file only.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License version 3,
// as published by the Free Software Foundation.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY.
// </copyright>


using System;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace RetryProxy.View.Behavior
{
    public static class ClipboardInterceptor
    {
        public static readonly DependencyProperty EnableSafeClipboardProperty =
            DependencyProperty.RegisterAttached(
                "EnableSafeClipboard",
                typeof(bool),
                typeof(ClipboardInterceptor),
                new PropertyMetadata(false, OnEnableSafeClipboardChanged));

        public static void SetEnableSafeClipboard(DependencyObject element, bool value)
            => element.SetValue(EnableSafeClipboardProperty, value);

        public static bool GetEnableSafeClipboard(DependencyObject element)
            => (bool)element.GetValue(EnableSafeClipboardProperty);

        public static readonly DependencyProperty EnableUrlPasteProperty =
            DependencyProperty.RegisterAttached(
                "EnableUrlPaste",
                typeof(bool),
                typeof(ClipboardInterceptor),
                new PropertyMetadata(false));

        public static void SetEnableUrlPaste(DependencyObject element, bool value)
            => element.SetValue(EnableUrlPasteProperty, value);

        public static bool GetEnableUrlPaste(DependencyObject element)
            => (bool)element.GetValue(EnableUrlPasteProperty);

        private static void OnEnableSafeClipboardChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            switch (d)
            {
                case TextBox tb when (bool)e.NewValue:
                    AddCommandBindingsToTextBox(tb);
                    break;
                case RichTextBox rtb when (bool)e.NewValue:
                    AddCommandBindingsToRichTextBox(rtb);
                    break;
                case DataGrid dg when (bool)e.NewValue:
                    AddCommandBindingsToDataGrid(dg);
                    break;
            }
        }

        private static void AddCommandBindingsToTextBox(TextBox tb)
        {
            tb.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, OnCopyTextBox));
            tb.CommandBindings.Add(new CommandBinding(ApplicationCommands.Cut, OnCutTextBox));
            tb.CommandBindings.Add(new CommandBinding(ApplicationCommands.Paste, OnPasteTextBox));
        }

        private static void AddCommandBindingsToRichTextBox(RichTextBox rtb)
        {
            rtb.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, OnCopyRichTextBox));
            rtb.CommandBindings.Add(new CommandBinding(ApplicationCommands.Cut, OnCutRichTextBox));
            rtb.CommandBindings.Add(new CommandBinding(ApplicationCommands.Paste, OnPasteRichTextBox));
        }

        private static void AddCommandBindingsToDataGrid(DataGrid dg)
        {
            dg.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, OnCopyDataGrid));
        }

        private static void OnCopyTextBox(object sender, ExecutedRoutedEventArgs e)
        {
            if (sender is not TextBox { SelectionLength: > 0 } tb)
            {
                return;
            }

            try
            {
                System.Windows.Forms.Clipboard.Clear();
                System.Windows.Forms.Clipboard.SetDataObject(tb.SelectedText, true);
            }
            catch
            {
                // ignored
            }

            e.Handled = true;
        }

        private static void OnCutTextBox(object sender, ExecutedRoutedEventArgs e)
        {
            if (sender is not TextBox { SelectionLength: > 0 } tb)
            {
                return;
            }

            try
            {
                System.Windows.Forms.Clipboard.Clear();
                System.Windows.Forms.Clipboard.SetDataObject(tb.SelectedText, true);
            }
            catch
            {
                // ignored
            }

            tb.SelectedText = string.Empty;
            e.Handled = true;
        }

        private static void OnPasteTextBox(object sender, ExecutedRoutedEventArgs e)
        {
            if (sender is not TextBox tb || tb.IsReadOnly || !tb.IsEnabled)
            {
                return;
            }

            string? pasteText;
            try
            {
                var data = Clipboard.GetDataObject();
                pasteText = data?.GetData(DataFormats.UnicodeText, false) as string
                    ?? data?.GetData(DataFormats.Text, false) as string;
                if (pasteText is null && GetEnableUrlPaste(tb))
                {
                    pasteText = ExtractUrlFromHtml(data?.GetData(DataFormats.Html, false) as string);
                }
            }
            catch (Exception error) when (error is ExternalException or InvalidOperationException)
            {
                return;
            }

            if (pasteText is null)
            {
                return;
            }

            var start = tb.SelectionStart;
            tb.SelectedText = pasteText;
            tb.CaretIndex = start + pasteText.Length;
            tb.SelectionLength = 0;
            e.Handled = true;
        }

        internal static string? ExtractUrlFromHtml(string? html)
        {
            if (string.IsNullOrEmpty(html) || html.Length > 256 * 1024)
            {
                return null;
            }

            try
            {
                var urls = Regex.Matches(html, "<a\\b[^>]*\\bhref\\s*=\\s*(?:\"(?<url>[^\"]+)\"|'(?<url>[^']+)'|(?<url>[^\\s>]+))", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200))
                    .Select(match => WebUtility.HtmlDecode(match.Groups["url"].Value).Trim())
                    .Where(url => Uri.TryCreate(url, UriKind.Absolute, out var parsed) && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
                    .Distinct(StringComparer.Ordinal)
                    .Take(2)
                    .ToArray();
                return urls.Length == 1 ? urls[0] : null;
            }
            catch (RegexMatchTimeoutException)
            {
                return null;
            }
        }

        private static void OnCopyRichTextBox(object sender, ExecutedRoutedEventArgs e)
        {
            if (sender is not RichTextBox rtb)
            {
                return;
            }

            var textRange = new TextRange(rtb.Selection.Start, rtb.Selection.End);
            if (string.IsNullOrEmpty(textRange.Text))
            {
                return;
            }

            try
            {
                System.Windows.Forms.Clipboard.Clear();
                System.Windows.Forms.Clipboard.SetDataObject(textRange.Text, true);
            }
            catch
            {
                // ignored
            }

            e.Handled = true;
        }

        private static void OnCutRichTextBox(object sender, ExecutedRoutedEventArgs e)
        {
            if (sender is not RichTextBox rtb)
            {
                return;
            }

            var selection = new TextRange(rtb.Selection.Start, rtb.Selection.End);
            if (string.IsNullOrEmpty(selection.Text))
            {
                return;
            }

            try
            {
                System.Windows.Forms.Clipboard.Clear();
                System.Windows.Forms.Clipboard.SetDataObject(selection.Text, true);
            }
            catch
            {
                // ignored
            }

            selection.Text = string.Empty;
            e.Handled = true;
        }

        private static void OnPasteRichTextBox(object sender, ExecutedRoutedEventArgs e)
        {
            if (sender is not RichTextBox rtb)
            {
                return;
            }

            if (!System.Windows.Forms.Clipboard.ContainsText())
            {
                return;
            }

            var pasteText = System.Windows.Forms.Clipboard.GetText();

            var selection = rtb.Selection;
            selection.Text = pasteText;

            var caretPos = selection.End;
            rtb.CaretPosition = caretPos;
            rtb.Selection.Select(caretPos, caretPos);

            e.Handled = true;
        }

        private static void OnCopyDataGrid(object sender, ExecutedRoutedEventArgs e)
        {
            if (sender is not DataGrid dg)
            {
                return;
            }
            ;

            // 获取选中单元格内容，拼成制表符分隔的文本
            var selectedCells = dg.SelectedCells;
            if (selectedCells == null || selectedCells.Count == 0)
            {
                return;
            }

            var sb = new System.Text.StringBuilder();
            var rowGroups = selectedCells.GroupBy(c => c.Item);

            foreach (var row in rowGroups)
            {
                var rowText = string.Join("\t", row.Select(cell =>
                {
                    if (cell.Column.GetCellContent(cell.Item) is TextBlock tb)
                    {
                        return tb.Text;
                    }

                    return string.Empty;
                }));
                sb.AppendLine(rowText);
            }

            var sbStr = sb.ToString().TrimEnd('\r', '\n');

            try
            {
                System.Windows.Forms.Clipboard.Clear();
                System.Windows.Forms.Clipboard.SetDataObject(sbStr, true);
            }
            catch
            {
                // ignored
            }

            e.Handled = true;
        }

    }
}
