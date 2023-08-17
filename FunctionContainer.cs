#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Web.Http;
using AutoMapper;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.CognitiveServices.Vision.ComputerVision;
using Microsoft.Azure.CognitiveServices.Vision.ComputerVision.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Extensions.SignalRService;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Rest;
using SeeSayMicroservices.Utils.Extensions;
using SeeSayMicroservices.Configuration;
using SeeSayMicroservices.Models.Dto;
using SeeSayMicroservices.Services.Abstractions;

namespace SeeSayMicroservices;

public class FunctionContainer
{
    private readonly ComputerVisionClient computerVisionClient;
    private readonly IDatabaseConnectionFactory connectionFactory;
    private readonly ILogger<FunctionContainer> logger;
    private readonly IMapper mapper;
    private readonly string ngrokUrl;

    public FunctionContainer(ComputerVisionClient computerVisionClient,
        IDatabaseConnectionFactory connectionFactory,
        ILogger<FunctionContainer> logger, IMapper mapper, IOptions<Ngrok> ngrokOptions)
    {
        this.computerVisionClient = computerVisionClient;
        this.connectionFactory = connectionFactory;
        this.logger = logger;
        this.mapper = mapper;
        ngrokUrl = ngrokOptions.Value.TunnelUrl;
    }

    [FunctionName("negotiate")]
    public static SignalRConnectionInfo GetSignalRInfo(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post")]
        HttpRequest req,
        [SignalRConnectionInfo(HubName = "chat")]
        SignalRConnectionInfo connectionInfo)
    {
        return connectionInfo;
    }

    [FunctionName("SendMessage")]
    public static Task SendMessage(
        [SignalRTrigger("chat", "messages", "SendMessage")]
        InvocationContext invocationContext,
        [SignalR(HubName = "chat")] IAsyncCollector<SignalRMessage> signalRMessages)
    {
        return signalRMessages.AddAsync(
            new SignalRMessage
            {
                Target = "newMessage",
                Arguments = invocationContext.Arguments
            });
    }

    [FunctionName("broadcast")]
    public static async Task Broadcast(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post")]
        object message,
        [SignalR(HubName = "chat")] IAsyncCollector<SignalRMessage> signalRMessages)
    {
        await signalRMessages.AddAsync(
            new SignalRMessage
            {
                Target = "newMessage",
                Arguments = new[] { message }
            });
    }


    [FunctionName("CheckImageForInappropriateContent")]
    public async Task<IActionResult> CheckImageForInappropriateContent(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post",
            Route = "check")]
        HttpRequest request,
        [Queue("description-tickets")] QueueClient descriptionTicketsQueue,
        [Blob("images/{rand-guid}.jpg")] BlobClient blobClient,
        [SignalR(HubName = "chat")] IAsyncCollector<SignalRMessage> signalRMessages)
    {
        var ticket = mapper.Map<IFormCollection, TicketDto>(request.Form);
        if (ticket is null)
            throw new InvalidOperationException("Invalid request body");
        
        // Read the image from the form data
        var imageFile = request.Form.Files["image"];
        if (imageFile is null)
            return new BadRequestResult();

        await using var imageStream = new MemoryStream();
        await imageFile.CopyToAsync(imageStream);
        imageStream.Position = 0;
        await using var imageStreamClone = new MemoryStream();
        await imageStream.CopyToAsync(imageStreamClone);

        logger.LogInformation(
            "{BaseLogMessage}: receive a request to check image for inappropriate content from user with ID {UserId}",
            GetBaseLogMessage(nameof(CheckImageForInappropriateContent)), ticket.UserId);
        await signalRMessages.AddAsync(
            new SignalRMessage
            {
                Target = "processing_start",
                Arguments = new object[] { "Uploading image..." },
                ConnectionId = ticket.SignalConnectionId
            }
        );

        ImageAnalysis response = default!;
        string? errorMessage = default;
        try
        {
            imageStream.Position = 0;
            response = await computerVisionClient.AnalyzeImageInStreamAsync(imageStream,
                new List<VisualFeatureTypes?>
                {
                    VisualFeatureTypes.Adult
                });
            imageStream.Close();
        }
        catch (ComputerVisionErrorResponseException exception)
        {
            errorMessage = exception.Body.Error.Message;
        }
        catch (Exception exception)
        {
            errorMessage = exception.Message;
        }

        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            logger.LogWarning(
                "{BaseLogMessage}: image was sent to the checking for inappropriate content, but the received response was unsuccessful. Message: {ErrorMessage}",
                GetBaseLogMessage(nameof(DescribeImage)), errorMessage);

            await signalRMessages.AddAsync(
                new SignalRMessage
                {
                    Target = "error_external",
                    Arguments = new object[] { "Error while uploading image. Please try later." }
                }
            );

            const string Query = "DELETE FROM Posts WHERE Id = @PostId";
            using var connection = connectionFactory.CreateConnection();
            await connection.ExecuteAsync(Query, new
            {
                ticket.PostId
            });

            return new InternalServerErrorResult();
        }

        if (response.Adult?.IsInappropriateContent() is true)
        {
            string query =
                    "UPDATE AspNetUsers SET LockoutEnabled = @NewLockoutEnabled WHERE Id = @UserId";
                using var connection = connectionFactory.CreateConnection();
                await connection.ExecuteAsync(query, new
                {
                    NewLockoutEnabled = true,
                    ticket.UserId
                });

                query = "DELETE FROM Posts WHERE Id = @PostId";
                await connection.ExecuteAsync(query, new
                {
                    ticket.PostId
                });
                connection.Close();
                
                logger.LogInformation(
                    "{BaseLogMessage}: user '{UserId}' has been banned for uploading an image with inappropriate content",
                    GetBaseLogMessage(nameof(CheckImageForInappropriateContent)), ticket.UserId);

                logger.LogWarning(
                "{BaseLogMessage}: image has been detected to contain inappropriate content. Removing an image from processing pipeline",
                GetBaseLogMessage(nameof(CheckImageForInappropriateContent)));

                await signalRMessages.AddAsync(
                    new SignalRMessage
                    {
                        Target = "error_ban",
                        Arguments = new object[] { "The image contains inappropriate content. You have been banned for violating the terms of service." },
                        ConnectionId = ticket.SignalConnectionId
                    });
                
            return new BadRequestResult();
        }

        logger.LogInformation(
            "{BaseLogMessage}: image was successfully checked for inappropriate content. Saving it",
            nameof(CheckImageForInappropriateContent));

        imageStreamClone.Position = 0;
        await blobClient.UploadAsync(imageStreamClone);

        logger.LogInformation(
            "{BaseLogMessage}: image was successfully saved at URL '{ImageUrl}'. Saving it to database",
            nameof(CheckImageForInappropriateContent), blobClient.Uri);

        var imageUrl = blobClient.Uri.ToString();
        await SaveImagePath(imageUrl, ticket.PostId);
        ticket.ImageUrl = imageUrl;

        if (ticket.ShouldAutoGenerateDescription)
        {
            logger.LogInformation(
                "{BaseLogMessage}: image was successfully processed, sending it next to processing pipeline to descripting",
                nameof(CheckImageForInappropriateContent));

            await descriptionTicketsQueue.SendMessageAsync(JsonSerializer.Serialize(ticket));
        }
        else
        {
            logger.LogInformation(
                "{BaseLogMessage}: image was successfully processed and its not requires generating description, processing finished",
                nameof(CheckImageForInappropriateContent));

            await signalRMessages.AddAsync(
                new SignalRMessage
                {
                    Target = "processing_finish",
                    Arguments = new object[] { "Image was successfully uploaded!" }
                }
            );   
        }

        return new OkResult();
    }

    [FunctionName("DescribeImage")]
    public async Task DescribeImage([QueueTrigger("description-tickets")] TicketDto ticket, [SignalR(HubName = "chat")] IAsyncCollector<SignalRMessage> signalRMessages)
    {
        ticket.ImageUrl = ticket.ImageUrl.ToNgrokUrl(ngrokUrl);
        logger.LogInformation(
            "{BaseLogMessage}: received a request to describe image by URL '{ImageUrl}'",
            GetBaseLogMessage(nameof(DescribeImage)), ticket.ImageUrl);

        string? errorMessage = null;
        IHttpOperationResponse<ImageDescription> response = default!;
        try
        {
            response = await computerVisionClient.DescribeImageWithHttpMessagesAsync(ticket.ImageUrl);
            if (!response.Response.IsSuccessStatusCode)
            {
                errorMessage = response.Response.ReasonPhrase;
            }
        }
        catch (ComputerVisionErrorResponseException exception)
        {
            errorMessage = exception.Body.Error.Message;
        }
        catch (Exception exception)
        {
            errorMessage = exception.Message;
        }
        
        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            logger.LogWarning(
                "{BaseLogMessage}: image '{ImageUrl}' was sent to the describing, but the received response was unsuccessful. Message: {ErrorMessage}",
                GetBaseLogMessage(nameof(DescribeImage)), ticket.ImageUrl,
                response.Response.ReasonPhrase);
        
        
            await signalRMessages.AddAsync(
                new SignalRMessage
                {
                    Target = "error_external",
                    Arguments = new object[] { "Error while uploading image. Please try later." },
                    ConnectionId = ticket.SignalConnectionId
                });
            
            return;
        }
        
        var description = response.Body.Captions.First()
            .Text;

        await SaveImageDescription(description, ticket.PostId);
        logger.LogInformation(
            "{BaseLogMessage}: successfully saved described image '{ImageUrl}' with description '{Description}'",
            GetBaseLogMessage(nameof(DescribeImage)), ticket.ImageUrl, description);

        await signalRMessages.AddAsync(
            new SignalRMessage
            {
                Target = "processing_finish",
                Arguments = new object[] { "Image was successfully uploaded!" },
                ConnectionId = ticket.SignalConnectionId
            });
    }

    #region Utils

    private async Task SaveImagePath(string imagePath, int postId)
    {
        const string Query =
            "UPDATE Posts SET ImagePath = @ImagePath WHERE Id = @PostId";
        using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(Query, new { ImagePath = imagePath, PostId = postId });
        connection.Close();
    }

    private async Task SaveImageDescription(string imageDescription, int postId)
    {
        const string Query = "UPDATE Posts SET Description = @ImageDescription WHERE Id = @PostId";
        using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(Query, new { ImageDescription = imageDescription, PostId = postId });
    }

    private static string GetBaseLogMessage(string methodName, HttpRequest? request = null)
    {
        var messageBuilder = new StringBuilder();
        messageBuilder.Append('[')
            .Append(request is not null ? request.Method : "UTILITY")
            .Append($"] {nameof(FunctionContainer)}.{methodName}");

        return messageBuilder.ToString();
    }

    #endregion
}