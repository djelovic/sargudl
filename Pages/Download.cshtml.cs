using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Sargudl.Pages;

public class DownloadModel : PageModel
{
    public string DownloadUrl { get; set; } = "";

    public void OnGet(string? url)
    {
        DownloadUrl = url ?? "";
    }
}
