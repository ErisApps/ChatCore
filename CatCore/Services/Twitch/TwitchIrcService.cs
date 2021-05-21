using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CatCore.Helpers;
using CatCore.Models.EventArgs;
using CatCore.Models.Shared;
using CatCore.Models.Twitch.IRC;
using CatCore.Services.Interfaces;
using CatCore.Services.Twitch.Interfaces;
using Serilog;

namespace CatCore.Services.Twitch
{
	internal class TwitchIrcService : ITwitchIrcService
	{
		private const string TWITCH_IRC_ENDPOINT = "wss://irc-ws.chat.twitch.tv:443";

		/// <remark>
		/// According to the official documentation, the rate limiting window interval is 30 seconds.
		/// However, due to delays in the connection/Twitch servers and this library being too precise time-wise,
		/// it might result in going over the rate limit again when it should have been reset.
		/// Resulting in a global temporary chat ban of 30 minutes, hence why we pick an internal time window of 32 seconds.
		/// </remark>
		private const long MESSAGE_SENDING_TIME_WINDOW_TICKS = 32 * TimeSpan.TicksPerSecond;

		private readonly ILogger _logger;
		private readonly IKittenWebSocketProvider _kittenWebSocketProvider;
		private readonly IKittenPlatformActiveStateManager _activeStateManager;
		private readonly ITwitchAuthService _twitchAuthService;
		private readonly ITwitchChannelManagementService _twitchChannelManagementService;

		private readonly char[] _ircMessageSeparator;

		private readonly ConcurrentQueue<(string channelName, string message)> _messageQueue;
		private readonly ConcurrentDictionary<string, long> _forcedSendChannelMessageSendDelays;
		private readonly List<long> _messageSendTimestamps;

		private readonly SemaphoreSlim _workerCanSleepSemaphoreSlim = new SemaphoreSlim(1, 1);
		private readonly SemaphoreSlim _workerSemaphoreSlim = new SemaphoreSlim(0, 1);

		private CancellationTokenSource? _messageQueueProcessorCancellationTokenSource;

		public TwitchIrcService(ILogger logger, IKittenWebSocketProvider kittenWebSocketProvider, IKittenPlatformActiveStateManager activeStateManager, ITwitchAuthService twitchAuthService,
			ITwitchChannelManagementService twitchChannelManagementService)
		{
			_logger = logger;
			_kittenWebSocketProvider = kittenWebSocketProvider;
			_activeStateManager = activeStateManager;
			_twitchAuthService = twitchAuthService;
			_twitchChannelManagementService = twitchChannelManagementService;

			_twitchAuthService.OnCredentialsChanged += TwitchAuthServiceOnOnCredentialsChanged;
			_twitchChannelManagementService.ChannelsUpdated += TwitchChannelManagementServiceOnChannelsUpdated;

			_ircMessageSeparator = new[] {'\r', '\n'};

			_messageQueue = new ConcurrentQueue<(string channelName, string message)>();
			_forcedSendChannelMessageSendDelays = new ConcurrentDictionary<string, long>();
			_messageSendTimestamps = new List<long>();
		}

		public event Action? OnLogin;
		public event Action<IChatChannel>? OnJoinChannel;
		public event Action<IChatChannel>? OnLeaveChannel;
		public event Action<IChatChannel>? OnRoomStateChanged;
		public event Action<IChatMessage>? OnMessageReceived;

		public void SendMessage(IChatChannel channel, string message)
		{
			_workerCanSleepSemaphoreSlim.Wait();
			_messageQueue.Enqueue((channel.Id, $"@id={Guid.NewGuid().ToString()} {IrcCommands.PRIVMSG} #{channel.Id} :{message}"));
			_workerCanSleepSemaphoreSlim.Release();

			// Trigger re-activation of worker thread
			if (_workerSemaphoreSlim.CurrentCount == 0)
			{
				_workerSemaphoreSlim.Release();
			}
		}

		async Task ITwitchIrcService.Start()
		{
			if (!_twitchAuthService.HasTokens)
			{
				return;
			}

			if (!_twitchAuthService.TokenIsValid)
			{
				await _twitchAuthService.RefreshTokens().ConfigureAwait(false);
			}

			_kittenWebSocketProvider.ConnectHappened -= ConnectHappenedHandler;
			_kittenWebSocketProvider.ConnectHappened += ConnectHappenedHandler;

			_kittenWebSocketProvider.DisconnectHappened -= DisconnectHappenedHandler;
			_kittenWebSocketProvider.DisconnectHappened += DisconnectHappenedHandler;

			_kittenWebSocketProvider.MessageReceived -= MessageReceivedHandler;
			_kittenWebSocketProvider.MessageReceived += MessageReceivedHandler;

			await _kittenWebSocketProvider.Connect(TWITCH_IRC_ENDPOINT).ConfigureAwait(false);
		}

		async Task ITwitchIrcService.Stop()
		{
			await _kittenWebSocketProvider.Disconnect("Requested by service manager").ConfigureAwait(false);

			_kittenWebSocketProvider.ConnectHappened -= ConnectHappenedHandler;
			_kittenWebSocketProvider.DisconnectHappened -= DisconnectHappenedHandler;
			_kittenWebSocketProvider.MessageReceived -= MessageReceivedHandler;
		}

		private async void TwitchAuthServiceOnOnCredentialsChanged()
		{
			if (_twitchAuthService.HasTokens)
			{
				if (_activeStateManager.GetState(PlatformType.Twitch))
				{
					await ((ITwitchIrcService) this).Start().ConfigureAwait(false);
				}
			}
			else
			{
				await ((ITwitchIrcService) this).Stop().ConfigureAwait(false);
			}
		}

		private void TwitchChannelManagementServiceOnChannelsUpdated(object sender, TwitchChannelsUpdatedEventArgs e)
		{
			if (_activeStateManager.GetState(PlatformType.Twitch))
			{
				foreach (var disabledChannel in e.DisabledChannels)
				{
					_kittenWebSocketProvider.SendMessage($"PART #{disabledChannel.Value}");
				}

				foreach (var enabledChannel in e.EnabledChannels)
				{
					_kittenWebSocketProvider.SendMessage($"JOIN #{enabledChannel.Value}");
				}
			}
		}

		private void ConnectHappenedHandler()
		{
			_kittenWebSocketProvider.SendMessage("CAP REQ :twitch.tv/tags twitch.tv/commands twitch.tv/membership");

			_kittenWebSocketProvider.SendMessage($"PASS oauth:{_twitchAuthService.AccessToken}");
			_kittenWebSocketProvider.SendMessage($"NICK {_twitchAuthService.LoggedInUser?.LoginName ?? "."}");
		}

		private void DisconnectHappenedHandler()
		{
			_messageQueueProcessorCancellationTokenSource?.Cancel();
			_messageQueueProcessorCancellationTokenSource = null;
		}

		private void MessageReceivedHandler(string message)
		{
			MessageReceivedHandlerInternal(message);
		}

		private void MessageReceivedHandlerInternal(string rawMessage)
		{
			// TODO: Investigate possibility to split a message string into ReadOnlySpans<char> types instead of strings again, would prevents unnecessary heap allocations which might in turn improve the throughput
			var messages = rawMessage.Split(_ircMessageSeparator, StringSplitOptions.RemoveEmptyEntries);

			foreach (var messageInternal in messages)
			{
				// Handle IRC messages here
				ParseIrcMessage(messageInternal, out var tags, out var prefix, out string commandType, out var channelName, out var message);
#if DEBUG
				_logger.Verbose("Tags count: {Tags}", tags?.Count.ToString() ?? "N/A");
				_logger.Verbose("Prefix: {Prefix}", prefix ?? "N/A");
				_logger.Verbose("CommandType: {CommandType}", commandType);
				_logger.Verbose("ChannelName: {ChannelName}", channelName ?? "N/A");
				_logger.Verbose("Message: {Message}", message ?? "N/A");
				_logger.Verbose("");
#endif

				HandleParsedIrcMessage(ref tags, ref prefix, ref commandType, ref channelName, ref message);
			}
		}

		// ReSharper disable once CognitiveComplexity
		private static void ParseIrcMessage(string messageInternal, out ReadOnlyDictionary<string, string>? tags, out string? prefix, out string commandType, out string? channelName, out string? message)
		{
			// Null-ing this here as I can't do that in the method signature
			tags = null;
			prefix = null;
			channelName = null;
			message = null;

			// Twitch IRC Message spec
			// https://ircv3.net/specs/extensions/message-tags

			var position = 0;
			int nextSpacePosition;

			var messageAsSpan = messageInternal.AsSpan();

			void SkipToNextNonSpaceCharacter(ref ReadOnlySpan<char> msg)
			{
				while (position < msg.Length && msg[position] == ' ')
				{
					position++;
				}
			}

			// Check for message tags
			if (messageAsSpan[0] == '@')
			{
				nextSpacePosition = messageAsSpan.IndexOf(' ');
				if (nextSpacePosition == -1)
				{
					throw new Exception("Invalid IRC Message");
				}

				var tagsAsSpan = messageAsSpan.Slice(1, nextSpacePosition - 1);

				var tagsDictInternal = new Dictionary<string, string>();

				var charSeparator = '=';
				var startPos = 0;

				ReadOnlySpan<char> keyTmp = null;
				for (var curPos = 0; curPos < tagsAsSpan.Length; curPos++)
				{
					if (tagsAsSpan[curPos] == charSeparator)
					{
						if (charSeparator == ';')
						{
							if (curPos != startPos)
							{
								tagsDictInternal[keyTmp.ToString()] = tagsAsSpan.Slice(startPos, curPos - startPos).ToString();
							}

							charSeparator = '=';
							startPos = curPos + 1;
						}
						else
						{
							keyTmp = tagsAsSpan.Slice(startPos, curPos - startPos);

							charSeparator = ';';
							startPos = curPos + 1;
						}
					}
				}

				tags = new ReadOnlyDictionary<string, string>(tagsDictInternal);

				position = nextSpacePosition + 1;
				SkipToNextNonSpaceCharacter(ref messageAsSpan);
				messageAsSpan = messageAsSpan.Slice(position);
				position = 0;
			}


			// Handle prefix
			if (messageAsSpan[position] == ':')
			{
				nextSpacePosition = messageAsSpan.IndexOf(' ');
				if (nextSpacePosition == -1)
				{
					throw new Exception("Invalid IRC Message");
				}

				prefix = messageAsSpan.Slice(1, (nextSpacePosition) - 1).ToString();

				position = nextSpacePosition + 1;
				SkipToNextNonSpaceCharacter(ref messageAsSpan);
				messageAsSpan = messageAsSpan.Slice(position);
				position = 0;
			}


			// Handle MessageType
			nextSpacePosition = messageAsSpan.IndexOf(' ');
			if (nextSpacePosition == -1)
			{
				if (messageAsSpan.Length > position)
				{
					commandType = messageAsSpan.ToString();
					return;
				}
			}

			commandType = messageAsSpan.Slice(0, nextSpacePosition).ToString();

			position = nextSpacePosition + 1;
			SkipToNextNonSpaceCharacter(ref messageAsSpan);
			messageAsSpan = messageAsSpan.Slice(position);
			position = 0;


			// Handle channelname and message
			var handledInLoop = false;
			while (position < messageAsSpan.Length)
			{
				if (messageAsSpan[position] == ':')
				{
					handledInLoop = true;

					// Handle message (extracting this first as we're going to do a lookback in order to determine the previous part)
					message = messageAsSpan.Slice(position + 1).ToString();

					// Handle everything before the colon as the channelname parameter
					while (--position > 0 && messageAsSpan[position] == ' ')
					{
					}

					if (position > 0)
					{
						channelName = messageAsSpan.Slice(0, position + 1).ToString();
					}

					break;
				}

				position++;
			}

			if (!handledInLoop)
			{
				channelName = messageAsSpan.ToString();
			}
		}

		// ReSharper disable once CognitiveComplexity
		// ReSharper disable once CyclomaticComplexity
		private void HandleParsedIrcMessage(ref ReadOnlyDictionary<string, string>? messageMeta, ref string? prefix, ref string commandType, ref string? channelName, ref string? message)
		{
			// Command official documentation: https://datatracker.ietf.org/doc/html/rfc1459 and https://datatracker.ietf.org/doc/html/rfc2812
			// Command Twitch documentation: https://dev.twitch.tv/docs/irc/commands
			// CommandMeta documentation: https://dev.twitch.tv/docs/irc/tags

			switch (commandType)
			{
				case IrcCommands.PING:
					_kittenWebSocketProvider.SendMessage($"{IrcCommands.PONG} :{message!}");
					break;
				case IrcCommands.RPL_ENDOFMOTD:
					OnLogin?.Invoke();
					foreach (var loginName in _twitchChannelManagementService.GetAllActiveLoginNames())
					{
						_kittenWebSocketProvider.SendMessage($"JOIN #{loginName}");
					}

					_messageQueueProcessorCancellationTokenSource?.Cancel();
					_messageQueueProcessorCancellationTokenSource = new CancellationTokenSource();

					_ = Task.Run(() => ProcessQueuedMessage(_messageQueueProcessorCancellationTokenSource.Token), _messageQueueProcessorCancellationTokenSource.Token).ConfigureAwait(false);

					// TODO: Remove this placeholder code... seriously... It's just here so the code would compile 😸
					if (prefix == "")
					{
						OnJoinChannel?.Invoke(null!);
						OnLeaveChannel?.Invoke(null!);
						OnRoomStateChanged?.Invoke(null!);
						OnMessageReceived?.Invoke(null!);
					}

					break;
				case IrcCommands.NOTICE:
					// MessageId for NOTICE documentation: https://dev.twitch.tv/docs/irc/msg-id

					break;
				case TwitchIrcCommands.USERNOTICE:
				case IrcCommands.PRIVMSG:
					break;
				case IrcCommands.JOIN:
					break;
				case IrcCommands.PART:
					break;
				case TwitchIrcCommands.ROOMSTATE:
					break;
				case TwitchIrcCommands.USERSTATE:
					break;
				case TwitchIrcCommands.GLOBALUSERSTATE:
					break;
				case TwitchIrcCommands.CLEARCHAT:
					break;
				case TwitchIrcCommands.CLEARMSG:
					break;
				case TwitchIrcCommands.RECONNECT:
					break;
				case TwitchIrcCommands.HOSTTARGET:
					break;
			}
		}

		// ReSharper disable once CognitiveComplexity
		private async Task ProcessQueuedMessage(CancellationToken cts)
		{
			long? GetTicksTillReset()
			{
				if (_messageQueue.IsEmpty)
				{
					return null;
				}

				var rateLimit = (int) GetRateLimit(_messageQueue.First().channelName);

				if (_messageSendTimestamps.Count < rateLimit)
				{
					return 0;
				}

				var ticksTillReset = _messageSendTimestamps[_messageSendTimestamps.Count - rateLimit] + MESSAGE_SENDING_TIME_WINDOW_TICKS - DateTime.UtcNow.Ticks;
				return ticksTillReset > 0 ? ticksTillReset : 0;
			}

			void UpdateRateLimitState()
			{
				while (_messageSendTimestamps.Count > 0 && DateTime.UtcNow.Ticks - _messageSendTimestamps.First() > MESSAGE_SENDING_TIME_WINDOW_TICKS)
				{
					_messageSendTimestamps.RemoveAt(0);
				}
			}

			bool CheckIfConsumable()
			{
				UpdateRateLimitState();

				return !_messageQueue.IsEmpty && _messageSendTimestamps.Count < (int) GetRateLimit(_messageQueue.First().channelName);
			}

			async Task HandleQueue()
			{
				while (_messageQueue.TryPeek(out var msg))
				{
					var rateLimit = GetRateLimit(msg.channelName);
					if (_messageSendTimestamps.Count >= (int) rateLimit)
					{
						_logger.Debug("Hit rate limit. Type {RateLimit}", rateLimit.ToString("G"));
						break;
					}

					if (_forcedSendChannelMessageSendDelays.TryGetValue(msg.channelName, out var ticksSinceLastChannelMessage))
					{
						var ticksTillReset = ticksSinceLastChannelMessage + (rateLimit == MessageSendingRateLimit.Relaxed ? 50 : 1250) * TimeSpan.TicksPerMillisecond - DateTime.UtcNow.Ticks;
						if (ticksTillReset > 0)
						{
							var msTillReset = (int) Math.Ceiling((double) ticksTillReset / TimeSpan.TicksPerMillisecond);
							_logger.Verbose("Delayed message sending, will send next message in {TimeTillReset}ms", msTillReset);
							await Task.Delay(msTillReset, CancellationToken.None).ConfigureAwait(false);
						}
					}

					_messageQueue.TryDequeue(out msg);

					// Send message
					await _kittenWebSocketProvider.SendMessageInstant(msg.message).ConfigureAwait(false);
					// TODO: Add forwarding to internal message received handler
					// _logger.Information(msg.message);

					var ticksNow = DateTime.UtcNow.Ticks;
					_messageSendTimestamps.Add(ticksNow);
					_forcedSendChannelMessageSendDelays[msg.channelName] = ticksNow;
				}
			}

			while (!cts.IsCancellationRequested)
			{
				await HandleQueue().ConfigureAwait(false);

				do
				{
					_logger.Verbose("Hibernating worker queue");

					await _workerCanSleepSemaphoreSlim.WaitAsync(CancellationToken.None);
					var canConsume = !_messageQueue.IsEmpty;
					_workerCanSleepSemaphoreSlim.Release();

					var remainingTicks = GetTicksTillReset();
					var autoReExecutionDelay = canConsume ? remainingTicks > 0 ? (int) Math.Ceiling((double) remainingTicks / TimeSpan.TicksPerMillisecond) : 0 : -1;
					_logger.Information("Auto re-execution delay: {AutoReExecutionDelay}ms", autoReExecutionDelay);

					await Task.WhenAny(
						Task.Delay(autoReExecutionDelay, cts),
						_workerSemaphoreSlim.WaitAsync(cts));

					_logger.Verbose("Waking up worker queue");
				} while (!CheckIfConsumable() && !cts.IsCancellationRequested);
			}

			_logger.Warning("Stopped worker queue");
		}

		private MessageSendingRateLimit GetRateLimit(string channelName)
		{
			// TODO: Add code to check for moderator rights on other channels
			return _twitchAuthService.LoggedInUser?.LoginName == channelName ? MessageSendingRateLimit.Relaxed : MessageSendingRateLimit.Normal;
		}
	}
}