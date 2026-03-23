using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Invocation;
using System.CommandLine.NamingConventionBinder;
using System.CommandLine.Parsing;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Wabbajack.Paths;

namespace Wabbajack.CLI.Builder;

public class CommandLineBuilder
{
    private static IServiceProvider _provider = null!;
    public CommandLineBuilder(IServiceProvider provider)
    {
        _provider = provider;
    }
    
    public async Task<int> Run(string[] args)
    {
        var root = new RootCommand();
        
        // Add global debug option
        var debugOption = new Option<bool>("--debug", "Enable debug logging");
        root.AddGlobalOption(debugOption);
        
        foreach (var verb in _commands)
        {
            root.Add(MakeCommend(verb.Type, verb.Handler, verb.Definition));
        }

        // Build command line with exception handler for better error messages
        var builder = new System.CommandLine.Builder.CommandLineBuilder(root)
            .UseDefaults()
            .UseExceptionHandler((exception, context) =>
            {
                // Provide user-friendly error messages based on exception type and context
                var userMessage = GetUserFriendlyMessage(exception);
                Console.Error.WriteLine($"Error: {userMessage}");
                
                // Show additional context if available (file paths, error codes, etc.)
                var additionalInfo = GetAdditionalContext(exception);
                if (!string.IsNullOrEmpty(additionalInfo))
                {
                    Console.Error.WriteLine(additionalInfo);
                }
                
                // Show inner exception if present (with user-friendly translation)
                var innerEx = exception.InnerException;
                var depth = 0;
                while (innerEx != null && depth < 2) // Limit depth to avoid excessive output
                {
                    var innerMessage = GetUserFriendlyMessage(innerEx);
                    if (innerMessage != innerEx.Message) // Only show if we translated it
                    {
                        Console.Error.WriteLine($"  Details: {innerMessage}");
                    }
                    innerEx = innerEx.InnerException;
                    depth++;
                }
                
                // Extract and show Exception.Data if present (file paths, error codes, etc.)
                if (exception.Data.Count > 0)
                {
                    Console.Error.WriteLine("\nAdditional information:");
                    foreach (System.Collections.DictionaryEntry entry in exception.Data)
                    {
                        Console.Error.WriteLine($"  {entry.Key}: {entry.Value}");
                    }
                }
                
                // Only show technical details in debug mode
                var isDebug = context.ParseResult.HasOption(debugOption);
                if (isDebug)
                {
                    Console.Error.WriteLine($"\nTechnical details:");
                    Console.Error.WriteLine($"  Exception type: {exception.GetType().Name}");
                    Console.Error.WriteLine($"  Original message: {exception.Message}");
                    Console.Error.WriteLine($"\nStack trace:\n{exception.StackTrace}");
                }
                else
                {
                    Console.Error.WriteLine("\nRun with --debug flag for technical details and stack trace.");
                }
                
                var structuredType = ClassifyException(exception);
                StructuredError.WriteError(structuredType, GetUserFriendlyMessage(exception));
                context.ExitCode = StructuredError.ExitCodeFor(structuredType);
            });

        // Build the parser and use it to invoke - this ensures exception handler is applied
        var parser = builder.Build();
        var parseResult = parser.Parse(args);
        return await parseResult.InvokeAsync();
    }

    private static Dictionary<Type, Func<OptionDefinition, Option>> _optionCtors = new()
    {
        {
            typeof(string),
            d => new Option<string>(d.Aliases, description: d.Description)
        },
        {
            typeof(int),
            d => new Option<int>(d.Aliases, description: d.Description)
        },
        {
            typeof(AbsolutePath),
            d => new Option<AbsolutePath>(d.Aliases, description: d.Description, parseArgument: d => d.Tokens.Single().Value.ToAbsolutePath())
        },
        {
            typeof(Uri),
            d => new Option<Uri>(d.Aliases, description: d.Description)
        },
        {
            typeof(bool),
            d => new Option<bool>(d.Aliases, description: d.Description)
        },
        
    };

    private Command MakeCommend(Type verbType, Func<object, Delegate> verbHandler, VerbDefinition definition)
    {
        var command = new Command(definition.Name, definition.Description);
        foreach (var option in definition.Options)
        {
            command.Add(_optionCtors[option.Type](option));
        }
        command.Handler = new HandlerDelegate(_provider, verbType, verbHandler);
        return command;
    }
    
    private class HandlerDelegate : ICommandHandler
    {
        private IServiceProvider _provider;
        private Type _type;
        private readonly Func<object, Delegate> _delgate;

        public HandlerDelegate(IServiceProvider provider, Type type, Func<object, Delegate> inner)
        {
            _provider = provider;
            _type = type;
            _delgate = inner;
        }
        public int Invoke(InvocationContext context)
        {
            var service = _provider.GetRequiredService(_type);
            var handler = CommandHandler.Create(_delgate(service));
            return handler.Invoke(context);
        }

        public Task<int> InvokeAsync(InvocationContext context)
        {
            var service = _provider.GetRequiredService(_type);
            var handler = CommandHandler.Create(_delgate(service));
            return handler.InvokeAsync(context);
        }
    }

    private static List<(Type Type, VerbDefinition Definition, Func<object, Delegate> Handler)> _commands { get; set; } = new();
    public static IEnumerable<Type> Verbs => _commands.Select(c => c.Type);

    public static void RegisterCommand<T>(VerbDefinition definition, Func<object, Delegate> handler)
    {
        _commands.Add((typeof(T), definition, handler));
        
    }
    
    /// <summary>
    /// Translates technical exception messages into user-friendly messages
    /// </summary>
    private static string GetUserFriendlyMessage(Exception exception)
    {
        var message = exception.Message;
        var exceptionType = exception.GetType().Name;
        
        // Handle common technical messages
        if (message.Contains("Sequence contains no matching element"))
        {
            // Check stack trace to provide context
            var stackTrace = exception.StackTrace ?? "";
            if (stackTrace.Contains("FileForArchiveHashPath") || stackTrace.Contains("VFS"))
            {
                // Extract file name from message if present (e.g., "Failed to look up file X by hash Y")
                if (message.Contains("Failed to look up file"))
                {
                    // Keep the original message as it's already user-friendly
                    return message;
                }
                return "A required file was not found in the installation. This may indicate a missing download, corrupted file, or version mismatch.";
            }
            return "A required item was not found. This may indicate missing files or a corrupted installation.";
        }
        
        if (message.Contains("FileNotFoundException") || message.Contains("Could not find file"))
        {
            // Extract file path from message
            var fileMatch = Regex.Match(message, @"['""]([^'""]+)['""]");
            if (fileMatch.Success)
            {
                return $"File not found: {fileMatch.Groups[1].Value}";
            }
            return "A required file was not found. Please check that all downloads completed successfully.";
        }
        
        if (message.Contains("UnauthorizedAccessException") || message.Contains("Access is denied"))
        {
            return "Permission denied. Please check file permissions and ensure you have write access to the installation directory.";
        }
        
        if (message.Contains("IOException") && message.Contains("No space left"))
        {
            return "Insufficient disk space. Please free up space and try again.";
        }
        
        if (message.Contains("InvalidOperationException") && message.Contains("Missing archive"))
        {
            // Extract hash from message if present
            var hashMatch = Regex.Match(message, @"hash ([A-Za-z0-9+/=]+)");
            if (hashMatch.Success)
            {
                return $"Required archive is missing (hash: {hashMatch.Groups[1].Value}). Please ensure all files have been downloaded successfully.";
            }
            return "A required archive is missing. Please ensure all downloads completed successfully.";
        }
        
        // For other exceptions, try to make the message more readable
        if (message.Contains("Interop.ThrowExceptionForIoErrno") || message.Contains("ENOENT"))
        {
            return "File or directory not found. Please check that all required files exist and paths are correct.";
        }
        
        if (message.Contains("ENOSPC"))
        {
            return "No space left on device. Please free up disk space and try again.";
        }
        
        if (message.Contains("EACCES"))
        {
            return "Permission denied. Please check file permissions.";
        }
        
        // If we can't translate it, return the original message (it might already be user-friendly)
        return message;
    }
    
    /// <summary>
    /// Extracts additional context from the exception (file paths, operation being performed, etc.)
    /// </summary>
    private static string GetAdditionalContext(Exception exception)
    {
        var context = new System.Text.StringBuilder();
        var stackTrace = exception.StackTrace ?? "";
        
        // Try to extract file paths from stack trace or message
        if (stackTrace.Contains("FileForArchiveHashPath") || exception.Message.Contains("Failed to look up file"))
        {
            // Extract file name and hash from message
            var fileMatch = Regex.Match(exception.Message, @"file ([^\s]+) by hash ([A-Za-z0-9+/=]+)");
            if (fileMatch.Success)
            {
                context.AppendLine($"  File: {fileMatch.Groups[1].Value}");
                context.AppendLine($"  Expected hash: {fileMatch.Groups[2].Value}");
            }
        }
        
        // Extract file path from FileNotFoundException
        if (exception is System.IO.FileNotFoundException fnfEx && !string.IsNullOrEmpty(fnfEx.FileName))
        {
            context.AppendLine($"  File path: {fnfEx.FileName}");
        }
        
        return context.ToString().TrimEnd();
    }

    /// <summary>
    /// Maps an exception to a structured error type string for the JE protocol.
    /// Walks the inner exception chain so wrapped exceptions are classified correctly.
    /// </summary>
    private static string ClassifyException(Exception exception)
    {
        var current = exception;
        while (current != null)
        {
            if (current is UnauthorizedAccessException)
                return StructuredError.ErrorType.PermissionDenied;

            if (current is System.IO.FileNotFoundException or System.IO.DirectoryNotFoundException)
                return StructuredError.ErrorType.FileNotFound;

            if (current is System.Net.Http.HttpRequestException httpEx)
            {
                var s = httpEx.Message;
                if (s.Contains("401") || s.Contains("403") || s.Contains("Unauthorized") || s.Contains("Forbidden"))
                    return StructuredError.ErrorType.AuthFailed;
                return StructuredError.ErrorType.NetworkError;
            }

            var msg = current.Message;
            if (msg.Contains("ENOSPC") || (msg.Contains("No space left")))
                return StructuredError.ErrorType.DiskFull;
            if (msg.Contains("EACCES") || msg.Contains("Access is denied"))
                return StructuredError.ErrorType.PermissionDenied;
            if (msg.Contains("Missing archive") || msg.Contains("Failed to look up file"))
                return StructuredError.ErrorType.FileNotFound;

            current = current.InnerException;
        }
        return StructuredError.ErrorType.EngineError;
    }
}

public record OptionDefinition(Type Type, string ShortOption, string LongOption, string Description)
{
    public string[] Aliases
    {
        get
        {
            if (string.IsNullOrEmpty(ShortOption))
                return new[] { "--" + LongOption };
            return new[] { "-" + ShortOption, "--" + LongOption };
        }
    }
}

public record VerbDefinition(string Name, string Description, OptionDefinition[] Options)
{
}

