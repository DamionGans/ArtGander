using ArtGander.Core.Services;

namespace ArtGander.Core.Tests;

public class HtmlTextTests
{
    [Fact]
    public void Keeps_the_simple_markup_of_shop_editors()
    {
        const string html = "<p>A <strong>happy</strong> <em>goose</em>, <u>honk</u> <s>quack</s>.</p><h2>Details</h2><ul><li>A3</li><li>Matte</li></ul><ol><li>One</li></ol><p>Line<br>two</p>";
        Assert.Equal(html, HtmlText.Clean(html));
    }

    [Fact]
    public void Removes_everything_else_but_keeps_the_text()
    {
        var clean = HtmlText.Clean("<div class=\"x\" style=\"color:red\">Hi <span>there</span><script>alert(1)</script> <img src=x onerror=y> <a href=\"javascript:alert(1)\" onclick=\"z\">link</a> <a href='https://goose.example'>shop</a></div>");
        Assert.Equal("<p>Hi there  <a>link</a> <a href=\"https://goose.example\">shop</a></p>", clean);
    }

    [Fact]
    public void Closes_tags_left_open_and_escapes_stray_brackets()
    {
        Assert.Equal("<p><strong>bold</strong></p>", HtmlText.Clean("<p><strong>bold"));
        Assert.Equal("<p>3 &lt; 4</p>", HtmlText.Clean("<p>3 < 4</p>"));
        Assert.Equal("plain text stays", HtmlText.Clean("plain text stays"));
    }

    [Fact]
    public void Converts_between_plain_text_and_html()
    {
        Assert.Equal("<p>Goose &amp; pond<br>line two</p>\n<p>Second</p>", HtmlText.FromPlainText("Goose & pond\nline two\n\nSecond"));
        Assert.Equal("Intro\n• A3\n• A2", HtmlText.ToPlainText("<p>Intro</p><ul><li>A3</li><li>A2</li></ul>"));
        Assert.Equal("Fish & chips", HtmlText.ToPlainText("<strong>Fish &amp; chips</strong>"));
    }
}
