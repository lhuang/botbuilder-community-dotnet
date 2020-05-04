﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Bot.Builder.Community.Adapters.RingCentral.Handoff;
using Microsoft.AspNetCore.Http;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static Bot.Builder.Community.Adapters.RingCentral.RingCentralConstants;

namespace Bot.Builder.Community.Adapters.RingCentral
{
    /// <summary>
    /// A Bot Builder adapter implementation used to handle RingCentral webhook HTTP requests.
    /// </summary>
    public class RingCentralAdapter : BotAdapter, IBotFrameworkHttpAdapter
    {
        private readonly RingCentralClientWrapper _ringCentralClient;
        private readonly ILogger _logger;
        private readonly IBotFrameworkHttpAdapter _botAdapter;
        private readonly IHandoffRequestRecognizer _handoffRequestRecognizer;

        /// <summary>
        /// Initializes a new instance of the <see cref="RingCentralAdapter"/> class.
        /// </summary>        
        /// <param name="ringCentralClient">RingCentralClient instance.</param>
        /// <param name="botAdapter">BotAdapter of the bot.</param>
        /// <param name="handoffRequestRecognizer">Recognizer to determine if it's a human or bot request.</param>
        /// <param name="logger">Logger instance.</param>
        public RingCentralAdapter(
            RingCentralClientWrapper ringCentralClient, 
            IBotFrameworkHttpAdapter botAdapter, 
            IHandoffRequestRecognizer handoffRequestRecognizer, 
            ILogger logger = null)
        {
            _ringCentralClient = ringCentralClient ?? throw new ArgumentNullException(nameof(ringCentralClient));
            _botAdapter = botAdapter ?? throw new ArgumentNullException(nameof(botAdapter));
            _handoffRequestRecognizer = handoffRequestRecognizer ?? throw new ArgumentNullException(nameof(handoffRequestRecognizer));
            _logger = logger ?? NullLogger.Instance;
        }

        /// <summary>
        /// Sends activities generated by the bot to RingCentral.
        /// </summary>
        /// <param name="turnContext">The context object for this turn.</param>
        /// <param name="activities">Activities to be sent to the RingCentral content API.</param>
        /// <param name="cancellationToken">Propagates notification that operations should be canceled.</param>
        /// <returns>Responses that contains resource id's.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="activities"/> is null.</exception>
        public override async Task<ResourceResponse[]> SendActivitiesAsync(ITurnContext turnContext, Activity[] activities, CancellationToken cancellationToken)
        {
            activities = activities ?? throw new ArgumentNullException(nameof(activities));
            var responses = new List<ResourceResponse>();

            foreach (var activity in activities)
            {
                if (activity.Type == ActivityTypes.Message)
                {
                    var hasChannelData = turnContext.Activity.TryGetChannelData<RingCentralChannelData>(out var channelData);
                    if (hasChannelData)
                    {
                        var resourceId = await _ringCentralClient.SendContentToRingCentralAsync(activity, channelData.SourceId);
                        responses.Add(new ResourceResponse() { Id = resourceId });
                    }
                    else
                    {
                        _logger.LogTrace($"SendActivitiesAsync: Required channel data of type {typeof(RingCentralChannelData)} is not present on message.");
                    }
                }
                else
                {
                    _logger.LogTrace(
                        "SendActivitiesAsync: Did not send activity with id '{ActivityId}' to RingCentral. Activity type '{NotSupportedActivityType}' is not supported. Only activities of type '{SupportedActivityType}' are supported.", 
                        activity.Id,
                        activity.Type,
                        ActivityTypes.Message);
                }
            }

            return responses.ToArray();
        }

        /// <summary>
        /// This method can be called from inside a POST method on any controller implementation.
        /// It handles RingCentral webhooks of different types.
        /// </summary>
        /// <param name="httpRequest">The HTTP request object, typically in a POST handler by a controller.</param>
        /// <param name="httpResponse">When this method completes, the HTTP response to send.</param>
        /// <param name="bot">The bot that will handle the incoming activity.</param>
        /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
        /// <returns>A task that represents the work queued to execute.</returns>
        /// <exception cref="ArgumentNullException">Either <paramref name="httpRequest"/>, <paramref name="httpResponse"/>, <paramref name="bot"/> is null.</exception>
        public async Task ProcessAsync(
            HttpRequest httpRequest, 
            HttpResponse httpResponse, 
            IBot bot, 
            CancellationToken cancellationToken = default)
        {
            _ = httpRequest ?? throw new ArgumentNullException(nameof(httpRequest));
            _ = httpResponse ?? throw new ArgumentNullException(nameof(httpResponse));
            _ = bot ?? throw new ArgumentNullException(nameof(bot));

            var (ringCentralRequestType, activity) = new Tuple<RingCentralHandledEvent, Activity>(RingCentralHandledEvent.VerifyWebhook, null);

            // CONSIDER: DetectRequestType method
            // CONSDIER: Should we prevent giving request and response out of hands here? (hard to know who and when this objects might change)

            if (httpRequest.Query != null && httpRequest.Query.ContainsKey("hub.mode"))
            {
                ringCentralRequestType = RingCentralHandledEvent.VerifyWebhook;
            }
            else
            {
                (ringCentralRequestType, activity) = await _ringCentralClient.GetActivityFromRingCentralRequestAsync(this, _botAdapter, httpRequest, httpResponse);
            }

            switch (ringCentralRequestType)
            {
                case RingCentralHandledEvent.VerifyWebhook:
                    await _ringCentralClient.VerifyWebhookAsync(httpRequest, httpResponse, cancellationToken);
                    break;
                case RingCentralHandledEvent.Intervention:
                case RingCentralHandledEvent.Action:
                    {
                        // Process human agent responses coming from RingCentral (Custom Source SDK)
                        if (activity != null)
                        {
                            using var context = new TurnContext(this, activity);
                            await RunPipelineAsync(context, bot.OnTurnAsync, cancellationToken).ConfigureAwait(false);
                        }
                        break;
                    }
                case RingCentralHandledEvent.ContentImported:
                    {
                        // Process messages created by any subscribed RingCentral source configured using a Webhook
                        if (activity != null)
                        {
                            var handoffRequestStatus = await _handoffRequestRecognizer.RecognizeHandoffRequestAsync(activity);

                            // Bot or Agent request -> Re-Categorize
                            if (handoffRequestStatus != HandoffTarget.None)
                            {
                                var channelData = activity.GetChannelData<RingCentralChannelData>();
                                var threadId = channelData.ThreadId;
                                var thread = await _ringCentralClient.GetThreadByIdAsync(threadId);

                                if (thread != null)
                                {
                                    await _ringCentralClient.HandoffConversationControlToAsync(handoffRequestStatus, thread);
                                }
                                else
                                {
                                    _logger.LogWarning("Could not handoff the conversation, thread with thread id \"{ThreadId}\" could not be found.", threadId);
                                }
                            }

                            // Bot or no specific request
                            if (handoffRequestStatus != HandoffTarget.Agent)
                            {
                                using var context = new TurnContext(this, activity);
                                await RunPipelineAsync(context, bot.OnTurnAsync, cancellationToken).ConfigureAwait(false);
                            }
                        }
                        break;
                    }
                case RingCentralHandledEvent.Unknown:
                default:
                    _logger.LogWarning($"Unsupported RingCentral Webhook or payload: '{httpRequest.PathBase}'.");
                    break;
            }
        }

        public override Task<ResourceResponse> UpdateActivityAsync(ITurnContext turnContext, Activity activity, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public override Task DeleteActivityAsync(ITurnContext turnContext, ConversationReference reference, CancellationToken cancellationToken)
            => throw new NotImplementedException();
    }
}