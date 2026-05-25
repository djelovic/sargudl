using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Sargudl.Services;

namespace Sargudl.Pages;

public class IndexModel : PageModel
{
    private readonly DownloadManager _manager;

    public IndexModel(DownloadManager manager)
    {
        _manager = manager;
    }

    public void OnGet() { }

    public IActionResult OnPost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            ModelState.AddModelError("url", "Please enter a valid http:// or https:// URL.");
            return Page();
        }

        _manager.StartOrGet(url);
        return RedirectToPage("Download", new { url });
    }
}
