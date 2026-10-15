using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using VDF.Web.Components.Layout;

namespace VDF.Web.Tests.Components;

public sealed class FolderBrowserTests : BunitContext {
    readonly string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "VDF.FolderBrowser." + Guid.NewGuid().ToString("N"))).FullName;

    protected override void Dispose(bool disposing) {
        base.Dispose(disposing);
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task TypedMissingPathCannotBeSelectedAndValidPathCanRecover() {
        string? selected = null;
        var page = Render<FolderBrowser>(p => p
            .Add(c => c.StartPaths, new[] { root })
            .Add(c => c.OnFolderSelected, EventCallback.Factory.Create<string>(this, path => selected = path)));
        await page.InvokeAsync(() => page.Instance.Open());
        await page.Find(".folder-path-bar input").InputAsync(Path.Combine(root, "missing"));
        await page.Find(".folder-footer .primary").ClickAsync(new MouseEventArgs());
        Assert.Null(selected);
        Assert.Contains("Directory not found", page.Markup);
        await page.Find(".folder-start-paths button").ClickAsync(new MouseEventArgs());
        await page.Find(".folder-footer .primary").ClickAsync(new MouseEventArgs());
        Assert.Equal(root, selected);
        Assert.Empty(page.FindAll(".folder-browser"));
    }

    [Fact]
    public async Task DisappearingFolderIsNotReturnedToCaller() {
        var child = Directory.CreateDirectory(Path.Combine(root, "temporary")).FullName;
        string? selected = null;
        var page = Render<FolderBrowser>(p => p
            .Add(c => c.OnFolderSelected, EventCallback.Factory.Create<string>(this, path => selected = path)));
        await page.InvokeAsync(() => page.Instance.Open(child));
        Directory.Delete(child);
        await page.Find(".folder-footer .primary").ClickAsync(new MouseEventArgs());
        Assert.Null(selected);
        Assert.Contains("Directory not found", page.Markup);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedPathCanRecoverFromNavigationOrSelectionErrorWithoutPressingGo(bool selectInvalidPath) {
        string? selected = null;
        var page = Render<FolderBrowser>(p => p
            .Add(c => c.OnFolderSelected, EventCallback.Factory.Create<string>(this, path => selected = path)));
        await page.InvokeAsync(() => page.Instance.Open(root));
        await page.Find(".folder-path-bar input").InputAsync(Path.Combine(root, "missing"));
        var action = selectInvalidPath ? page.Find(".folder-footer .primary") : page.FindAll(".folder-path-bar button").Last();
        await action.ClickAsync(new MouseEventArgs());
        Assert.Null(selected);
        Assert.Contains("Directory not found", page.Markup);
        await page.Find(".folder-path-bar input").InputAsync(root);
        Assert.False(page.Find(".folder-footer .primary").HasAttribute("disabled"));
        await page.Find(".folder-footer .primary").ClickAsync(new MouseEventArgs());
        Assert.Equal(root, selected);
        Assert.Empty(page.FindAll(".folder-browser"));
    }

    [Fact]
    public async Task ExistingBrowserWithoutScanPathsStillOpensAndNavigates() {
        var child = Directory.CreateDirectory(Path.Combine(root, "child")).FullName;
        var page = Render<FolderBrowser>();
        await page.InvokeAsync(() => page.Instance.Open(root));
        Assert.Empty(page.FindAll(".folder-start-paths"));
        await page.Find(".folder-item").ClickAsync(new MouseEventArgs());
        Assert.Equal(child, page.Find(".folder-path-bar input").GetAttribute("value"));
        await page.Find("button[title='Go up']").ClickAsync(new MouseEventArgs());
        Assert.Equal(root, page.Find(".folder-path-bar input").GetAttribute("value"));
    }
}
