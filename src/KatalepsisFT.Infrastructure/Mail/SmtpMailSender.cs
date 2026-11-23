using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using KatalepsisFT.Application.Reports;
using Microsoft.Extensions.Configuration;

namespace KatalepsisFT.Infrastructure.Mail;

/// <summary>
/// Sends mail through an SMTP account given to the server as environment values: SMTP_HOST, SMTP_PORT (587 when
/// left out), SMTP_USER, SMTP_PASSWORD and MAIL_FROM. Any provider that offers SMTP works. Without a host and a
/// sender address nothing is sent and the screens say so.
/// </summary>
public sealed class SmtpMailSender(IConfiguration configuration) : IMailSender
{
    public const string HostKey = "SMTP_HOST";
    public const string PortKey = "SMTP_PORT";
    public const string UserKey = "SMTP_USER";
    public const string PasswordKey = "SMTP_PASSWORD";
    public const string FromKey = "MAIL_FROM";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private string? Host => Clean(configuration[HostKey]);

    private string? From => Clean(configuration[FromKey]);

    public bool IsConfigured => Host is not null && From is not null && MailAddress.TryCreate(From, out _);

    public async Task SendAsync(MailToSend mail, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new MailException("The server has no mail account to send from yet.");
        }

        if (mail.To.Count == 0)
        {
            throw new MailException("There is nobody to send to.");
        }

        int port = int.TryParse(configuration[PortKey], out int given) ? given : 587;
        using MailMessage message = new() { From = new MailAddress(From!), Subject = mail.Subject, Body = mail.HtmlBody, IsBodyHtml = true };
        foreach (string to in mail.To)
        {
            message.To.Add(to);
        }

        List<MemoryStream> streams = [];
        try
        {
            foreach (MailAttachment attachment in mail.Attachments)
            {
                MemoryStream stream = new(attachment.Content);
                streams.Add(stream);
                message.Attachments.Add(new Attachment(stream, attachment.FileName, new ContentType(attachment.ContentType).MediaType));
            }

            using SmtpClient client = new(Host, port) { EnableSsl = true, Timeout = (int)Timeout.TotalMilliseconds };
            string? user = Clean(configuration[UserKey]);
            if (user is not null)
            {
                client.Credentials = new NetworkCredential(user, configuration[PasswordKey] ?? string.Empty);
            }

            using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(Timeout);
            await client.SendMailAsync(message, limit.Token);
        }
        catch (SmtpException e)
        {
            // The provider's own words say what is wrong (bad password, sender not allowed); they carry no secret.
            throw new MailException($"The mail server refused: {e.StatusCode}. {e.Message}", e);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MailException("The mail server did not answer in time.");
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or System.Security.Authentication.AuthenticationException)
        {
            throw new MailException($"The mail server could not be reached: {e.Message}", e);
        }
        finally
        {
            foreach (MemoryStream stream in streams)
            {
                await stream.DisposeAsync();
            }
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
