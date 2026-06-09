using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Sargudl.Services;

namespace Sargudl.Pages;

public class IndexModel(DownloadManager manager) : PageModel
{
    private readonly DownloadManager _manager = manager;

    public void OnGet() { }

    public async Task<IActionResult> OnPost(string? url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            ModelState.AddModelError("url", "Please enter a valid http:// or https:// URL.");
            return Page();
        }

        // Start the download here, then Post/Redirect/Get to the status page so a
        // browser refresh re-issues a harmless GET instead of re-submitting the form.
        await _manager.StartAsync(url, ct);
        return RedirectToPage("Download", new { url });
    }
}
