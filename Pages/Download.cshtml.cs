using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Sargudl.Pages;

public class DownloadModel : PageModel
{
    public string DownloadUrl { get; set; } = "";

    // Set when the page was reached via POST (the Index form). A plain GET
    // navigation leaves this false, so the page just shows current status.
    public bool AutoStart { get; set; }

    public void OnGet(string? url)
    {
        DownloadUrl = url ?? "";
        AutoStart = false;
    }

    public void OnPost(string? url)
    {
        DownloadUrl = url ?? "";
        AutoStart = true;
    }
}
