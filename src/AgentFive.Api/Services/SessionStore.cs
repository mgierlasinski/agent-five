using System.Collections.Concurrent;
using System.Text.Json;
using AgentFive.Services.OpenRouter;

namespace AgentFive.Api.Services;

public class SessionStore
{
    private readonly string _sessionDirectory;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public SessionStore(IHostEnvironment hostEnvironment)
    {
        _sessionDirectory = Path.Combine(hostEnvironment.ContentRootPath, "sessions");
        Directory.CreateDirectory(_sessionDirectory);
    }

    public async Task<T> RunExclusiveAsync<T>(
        string sessionId,
        string systemPrompt,
        Func<List<ChatMessage>, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var sessionPath = GetSessionPath(sessionId);
        var sessionLock = GetSessionLock(sessionId);
        await sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var messages = await LoadUnlockedAsync(sessionPath, systemPrompt, cancellationToken).ConfigureAwait(false);
            try
            {
                return await action(messages).ConfigureAwait(false);
            }
            finally
            {
                await PersistAsync(sessionPath, messages, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            sessionLock.Release();
        }
    }

    private async Task<List<ChatMessage>> LoadUnlockedAsync(string sessionPath, string systemPrompt, CancellationToken cancellationToken)
    {
        if (!File.Exists(sessionPath))
        {
            return new List<ChatMessage>
            {
                new("system", systemPrompt)
            };
        }

        await using var stream = File.OpenRead(sessionPath);
        var transcript = await JsonSerializer.DeserializeAsync<SessionTranscript>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
        var messages = transcript?.Messages?.Select(message => message.ToChatMessage()).ToList() ?? new List<ChatMessage>();

        if (messages.Count == 0 || !string.Equals(messages[0].Role, "system", StringComparison.OrdinalIgnoreCase))
        {
            messages.Insert(0, new ChatMessage("system", systemPrompt));
        }
        else if (!string.Equals(messages[0].Content, systemPrompt, StringComparison.Ordinal))
        {
            messages[0] = new ChatMessage("system", systemPrompt);
        }

        return messages;
    }

    private async Task PersistAsync(string sessionPath, IReadOnlyCollection<ChatMessage> messages, CancellationToken cancellationToken)
    {
        var transcript = new SessionTranscript(messages.Select(SessionMessage.FromChatMessage).ToList());
        await using var stream = File.Create(sessionPath);
        await JsonSerializer.SerializeAsync(stream, transcript, _jsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private string GetSessionPath(string sessionId)
    {
        var safeSessionId = string.Concat(sessionId.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        return Path.Combine(_sessionDirectory, $"{safeSessionId}.json");
    }

    private SemaphoreSlim GetSessionLock(string sessionId) => _locks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
}