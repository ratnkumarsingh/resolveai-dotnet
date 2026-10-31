using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace ResolveAI.E2E;

/// <summary>Starts its own server with live Claude as the default, so it runs apart from the tests using the shared one.</summary>
[CollectionDefinition("live-default", DisableParallelization = true)]
public sealed class LiveDefaultCollection;

[Collection("live-default")]
public sealed class LiveDefaultTests
{
    [Fact]
    public async Task One_click_on_the_home_page_never_starts_a_paid_run()
    {
        var live = new AppFixture { LiveByDefault = true };
        await live.InitializeAsync();
        try
        {
            var context = await live.Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = live.BaseUrl });
            var page = await context.NewPageAsync();
            await page.GotoAsync("/");
            await Expect(page.GetByTestId("investigate-now")).ToHaveCountAsync(0);
            await page.GetByTestId("spotlight-open").ClickAsync();
            await Expect(page).ToHaveURLAsync(new Regex("/incidents/INC-1046$"));
            Assert.Equal(0, live.Deps.Db.Get<int>("SELECT COUNT(*) FROM investigations"));
        }
        finally
        {
            await live.DisposeAsync();
        }
    }
}
