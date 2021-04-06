using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Mathematics;
using BenchmarkDotNet.Order;

namespace CatCoreBenchmarkSandbox.Benchmarks
{
	[MediumRunJob(RuntimeMoniker.Net472, Jit.LegacyJit, Platform.X64)]
	[Orderer(SummaryOrderPolicy.FastestToSlowest)]
	[RankColumn(NumeralSystem.Stars)]
	[MemoryDiagnoser]
	[CategoriesColumn, AllStatisticsColumn, BaselineColumn, MinColumn, Q1Column, MeanColumn, Q3Column, MaxColumn, MedianColumn]
	public class TwitchIrcMessageCompoundDeconstructionBenchmark
	{
		private readonly Regex _twitchMessageRegex =
			new Regex(
				@"^(?:@(?<Tags>[^\r\n ]*) +|())(?::(?<HostName>[^\r\n ]+) +|())(?<MessageType>[^\r\n ]+)(?: +(?<ChannelName>[^:\r\n ]+[^\r\n ]*(?: +[^:\r\n ]+[^\r\n ]*)*)|())?(?: +:(?<Message>[^\r\n]*)| +())?[\r\n]*$",
				RegexOptions.Compiled);
		private readonly Regex _tagRegex = new Regex(@"(?<Tag>[^@^;^=]+)=(?<Value>[^;\s]+)", RegexOptions.Compiled | RegexOptions.Multiline);


		[Params(
			":tmi.twitch.tv 376 realeris :>",
			":realeris!realeris@realeris.tmi.twitch.tv JOIN #realeris",
			":realeris.tmi.twitch.tv 353 realeris = #realeris :realeris",
			":realeris.tmi.twitch.tv 366 realeris #realeris :End of /NAMES list",
			":tmi.twitch.tv CAP * ACK :twitch.tv/tags twitch.tv/commands twitch.tv/membership",
			"@badge-info=subscriber/1;badges=broadcaster/1,subscriber/0;client-nonce=1ef9899702c12a2081fa33899d7e8465;color=#FF69B4;display-name=RealEris;emotes=;flags=;id=b4595e1c-dd1b-4e45-b7df-a3403c945ad6;mod=0;room-id=405499635;subscriber=1;tmi-sent-ts=1614390981294;turbo=0;user-id=405499635;user-type= :realeris!realeris@realeris.tmi.twitch.tv PRIVMSG #realeris :Heya",
			"@badge-info=founder/13;badges=moderator/1,founder/0,bits/1000;client-nonce=05e5fe0b80aadc4c5035303b99d6762a;color=#DAA520;display-name=Scarapter;emotes=;flags=;id=7317d5aa-38ae-4191-88d7-d4d54a3c27bc;mod=1;room-id=62975335;subscriber=0;tmi-sent-ts=1617644034348;turbo=0;user-id=51591450;user-type=mod :scarapter!scarapter@scarapter.tmi.twitch.tv PRIVMSG #lnterz :i definitely dont miss the mass of random charging ports that existed",
			"@badge-info=;badges=;color=;display-name=bonkeybob;emotes=;flags=;id=108b80a1-7829-4879-86cc-953c3a6b122b;mod=0;room-id=62975335;subscriber=0;tmi-sent-ts=1617644112658;turbo=0;user-id=549616012;user-type= :bonkeybob!bonkeybob@bonkeybob.tmi.twitch.tv PRIVMSG #lnterz :The phrase âitâs just a gameâ is such a weak mindset. You are ok with what happened, losing, imperfection of a craft. When you stop getting angry after losing, youâve lost twice.   Thereâs always something to learn, and always room for improvement, never settle.")]
		public string IrcMessage;

		[Benchmark(Baseline = true)]
		public void RegexBenchmark()
		{
			var match = _twitchMessageRegex.Match(IrcMessage);

			var tags = match.Groups["Tags"].Success ? new ReadOnlyDictionary<string, string>(_tagRegex.Matches(match.Value).Cast<Match>().Aggregate(new Dictionary<string, string>(), (dict, m) =>
			{
				dict[m.Groups["Tag"].Value] = m.Groups["Value"].Value;
				return dict;
			})) : null;
			var userName = match.Groups["HostName"].Success ? match.Groups["HostName"].Value.Split('!')[0] : null;
			var messageType = match.Groups["MessageType"].Value;
			var channelName = match.Groups["ChannelName"].Success ? match.Groups["ChannelName"].Value.Trim('#') : null;
			var message = match.Groups["Message"].Success ? match.Groups["Message"].Value : null;
		}

		[Benchmark]
		// ReSharper disable once CognitiveComplexity
		public void SpanDissectionBenchmark()
		{
			// Twitch IRC Message spec
			// https://ircv3.net/specs/extensions/message-tags

			// Null-ing this here as I can't do that in the method signature
			ReadOnlyDictionary<string, string>? tags = null;
			string? prefix = null;
			string commandType = null!;
			string? channelName = null;
			string? message = null;

			// Twitch IRC Message spec
			// https://ircv3.net/specs/extensions/message-tags

			var position = 0;
			var nextSpacePosition = 0;

			var messageAsSpan = IrcMessage.AsSpan();

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

				string? keyTmp = null;
				for (var curPos = 0; curPos < tagsAsSpan.Length; curPos++)
				{
					if (tagsAsSpan[curPos] == charSeparator)
					{
						if (charSeparator == ';')
						{
							tagsDictInternal[keyTmp!] = (curPos == startPos) ? string.Empty : tagsAsSpan.Slice(startPos, curPos - startPos - 1).ToString();

							charSeparator = '=';
							startPos = curPos + 1;
						}
						else
						{
							keyTmp = tagsAsSpan.Slice(startPos, curPos - startPos - 1).ToString();

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
					while (--position < messageAsSpan.Length && messageAsSpan[position] == ' ')
					{
					}

					channelName = messageAsSpan.Slice(0, position + 1).ToString();
					break;
				}

				position++;
			}

			if (!handledInLoop)
			{
				channelName = messageAsSpan.ToString();
			}
		}
	}
}