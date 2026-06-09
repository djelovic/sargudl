using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Sargudl.Pages;

public class IndexModel : PageModel
{
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

        return RedirectToPagePreserveMethod("Download", routeValues: new { url });
    }
}
