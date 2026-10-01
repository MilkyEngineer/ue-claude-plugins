// Copyright Alex Stevens (@MilkyEngineer). All Rights Reserved.

using Microsoft.Extensions.Logging;

namespace AgentKit.Core;

/// <summary>
/// The host's logger: plain lines, no categories or timestamps. Information and below go to the output writer; warnings and
/// errors go to the error writer, prefixed "warning: " and "error: ". Debug and trace show only at <see cref="LogLevel.Debug"/>
/// or lower (uak -verbose).
/// </summary>
public sealed class UakConsoleLogger(TextWriter output, TextWriter error, LogLevel minimumLevel = LogLevel.Information) : ILogger
{
	readonly object Gate = new();

	/// <summary>A logger over the process's console.</summary>
	public static UakConsoleLogger CreateConsole(LogLevel minimumLevel = LogLevel.Information) => new(Console.Out, Console.Error, minimumLevel);

	/// <inheritdoc/>
	public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

	/// <inheritdoc/>
	public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= minimumLevel;

	/// <inheritdoc/>
	public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
	{
		if (!IsEnabled(logLevel))
		{
			return;
		}
		string Message = formatter(state, exception);
		if (exception is not null && !Message.Contains(exception.Message, StringComparison.Ordinal))
		{
			Message = Message.Length == 0 ? exception.Message : Message + ": " + exception.Message;
		}
		if (exception is not null && minimumLevel <= LogLevel.Debug)
		{
			Message += Environment.NewLine + exception;
		}
		lock (Gate)
		{
			switch (logLevel)
			{
				case LogLevel.Warning:
					error.WriteLine("warning: " + Message);
					break;
				case LogLevel.Error:
				case LogLevel.Critical:
					error.WriteLine("error: " + Message);
					break;
				default:
					output.WriteLine(Message);
					break;
			}
		}
	}
}
