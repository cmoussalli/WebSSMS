using Microsoft.AspNetCore.Http.Features;
using WebSSMS.Models;
using WebSSMS.Services;

namespace WebSSMS.Endpoints;

/// <summary>
/// Plain HTTP endpoints for the actual bytes of a backup file.
///
/// These deliberately sit outside the Blazor circuit. Pushing a multi-gigabyte
/// .bak over SignalR would mean buffering it in browser memory and fighting the
/// hub message size limit; a normal HTTP request gets native download progress,
/// resumable-friendly semantics, and streaming in both directions.
///
/// Authorisation is the ticket: it is minted inside the circuit only after the
/// path has been validated, and it expires. No client-supplied path reaches disk.
/// </summary>
public static class BackupTransferEndpoints
{
    public static IEndpointRouteBuilder MapBackupTransferEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/backup/download/{ticketId}", DownloadAsync)
            .DisableAntiforgery();

        endpoints.MapPost("/api/backup/upload/{ticketId}", UploadAsync)
            .DisableAntiforgery();

        return endpoints;
    }

    private static async Task DownloadAsync(
        string ticketId,
        BackupTransferTicketStore store,
        HttpContext context,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("WebSSMS.BackupTransfer");

        var ticket = store.Take(ticketId, BackupTransferKind.Download);
        if (ticket?.WriteToAsync == null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("This download link is unknown or has expired. Request the file again.");
            return;
        }

        context.Response.ContentType = "application/octet-stream";
        context.Response.Headers.ContentDisposition =
            $"attachment; filename=\"{SanitiseHeaderValue(ticket.FileName)}\"";

        // Only advertise a length that is exact. An estimate here is worse than no
        // header at all: the browser would stop at the advertised byte count and
        // save a truncated file that still looks like a valid download.
        if (ticket.SizeIsExact && ticket.SizeBytes is > 0)
            context.Response.ContentLength = ticket.SizeBytes;

        try
        {
            await ticket.WriteToAsync(context.Response.Body, context.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Backup download of {File} was cancelled by the client.", ticket.FilePath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Backup download of {File} failed.", ticket.FilePath);

            // Headers are already on the wire by now, so the only honest signal
            // left is to abort the connection and let the browser fail the file.
            context.Abort();
        }
    }

    private static async Task<IResult> UploadAsync(
        string ticketId,
        BackupTransferTicketStore store,
        HttpContext context,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("WebSSMS.BackupTransfer");

        var ticket = store.Take(ticketId, BackupTransferKind.Upload);
        if (ticket?.ReadFromAsync == null)
            return Results.NotFound(new { error = "This upload link is unknown or has expired. Try again." });

        // Kestrel caps request bodies at 30 MB by default, which no real backup
        // file respects. The ticket's own MaxBytes is what actually bounds this.
        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
            sizeFeature.MaxRequestBodySize = null;

        try
        {
            var written = await ticket.ReadFromAsync(context.Request.Body, context.RequestAborted);
            store.Remove(ticketId);

            logger.LogInformation("Uploaded {Bytes} bytes to {File}.", written, ticket.FilePath);

            return Results.Ok(new
            {
                path = ticket.FilePath,
                fileName = ticket.FileName,
                bytes = written,
                size = BackupFileEntry.FormatSize(written)
            });
        }
        catch (OperationCanceledException)
        {
            store.Remove(ticketId);
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception ex)
        {
            store.Remove(ticketId);
            logger.LogError(ex, "Backup upload to {File} failed.", ticket.FilePath);
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Keeps quotes and control characters out of the Content-Disposition header.</summary>
    private static string SanitiseHeaderValue(string fileName)
    {
        var cleaned = new string(fileName.Where(c => !char.IsControl(c) && c != '"').ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "backup.bak" : cleaned;
    }
}
