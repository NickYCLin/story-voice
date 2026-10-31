using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using StoryVoice.Application.BookImports;
using StoryVoice.Application.Insights;
using StoryVoice.Application.Narrations;
using StoryVoice.Application.Narrations.SpeechPlanning;
using StoryVoice.Application.Series;

namespace StoryVoice.Api;

public sealed class ApiExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var statusCode = exception switch
        {
            UnsupportedBookFormatException => StatusCodes.Status415UnsupportedMediaType,
            BookTextUnavailableException => StatusCodes.Status409Conflict,
            NarrationTextUnavailableException => StatusCodes.Status409Conflict,
            SingleVoiceNarrationRetiredException => StatusCodes.Status409Conflict,
            NarrationAdmissionDisabledException => StatusCodes.Status409Conflict,
            NarrationRightsRequiredException => StatusCodes.Status400BadRequest,
            LocalLlmCharacterAnalysisInputTooLargeException => StatusCodes.Status413PayloadTooLarge,
            LocalLlmCharacterAnalysisSourceChangedException => StatusCodes.Status409Conflict,
            LocalLlmCharacterAnalysisUnavailableException => StatusCodes.Status503ServiceUnavailable,
            SeriesVoicePreviewUnavailableException => StatusCodes.Status503ServiceUnavailable,
            LocalClonePreviewUnavailableException => StatusCodes.Status503ServiceUnavailable,
            SpeechSegmentPreviewUnsupportedProviderException => StatusCodes.Status422UnprocessableEntity,
            AntiforgeryValidationException => StatusCodes.Status400BadRequest,
            ArgumentException or InvalidOperationException => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status500InternalServerError
        };

        if (statusCode == StatusCodes.Status500InternalServerError)
        {
            logger.LogError(exception, "Unhandled API exception");
        }
        else
        {
            // 4xx／503 也要留下伺服器端紀錄：這條路徑會把 exception.Message 回給
            // 客戶端，若完全不記錄，框架丟出的 InvalidOperationException（EF、LINQ
            // 轉譯失敗等真正的 bug）會被無聲吞成 400，永遠查不到。
            logger.LogWarning(
                exception,
                "API request failed with {StatusCode} on {Method} {Path}",
                statusCode,
                httpContext.Request.Method,
                httpContext.Request.Path);
        }

        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = statusCode switch
            {
                StatusCodes.Status400BadRequest => "Request validation failed",
                StatusCodes.Status409Conflict => "Book text is unavailable",
                StatusCodes.Status413PayloadTooLarge => "Book text exceeds local LLM analysis limits",
                StatusCodes.Status415UnsupportedMediaType => "Book format is not supported",
                StatusCodes.Status422UnprocessableEntity => "Segment preview is not supported for this voice provider",
                StatusCodes.Status503ServiceUnavailable => "Local LLM analysis is temporarily unavailable",
                _ => "The service could not complete the request"
            },
            Detail = statusCode < StatusCodes.Status500InternalServerError
                ? exception.Message
                : "StoryVoice 暫時無法完成要求，請稍後再試。"
        };
        if (exception is BookTextUnavailableException)
        {
            problemDetails.Extensions["code"] = BookTextUnavailableException.StableCode;
        }
        else if (exception is LocalLlmCharacterAnalysisInputTooLargeException)
        {
            problemDetails.Extensions["code"] = LocalLlmCharacterAnalysisInputTooLargeException.StableCode;
        }
        else if (exception is LocalLlmCharacterAnalysisSourceChangedException)
        {
            problemDetails.Extensions["code"] = LocalLlmCharacterAnalysisSourceChangedException.StableCode;
        }
        else if (exception is LocalLlmCharacterAnalysisUnavailableException)
        {
            problemDetails.Extensions["code"] = LocalLlmCharacterAnalysisUnavailableException.StableCode;
        }
        else if (exception is NarrationTextUnavailableException)
        {
            problemDetails.Extensions["code"] = NarrationTextUnavailableException.StableCode;
        }
        else if (exception is SingleVoiceNarrationRetiredException)
        {
            problemDetails.Extensions["code"] = SingleVoiceNarrationRetiredException.StableCode;
        }
        else if (exception is NarrationAdmissionDisabledException)
        {
            problemDetails.Extensions["code"] = NarrationAdmissionDisabledException.StableCode;
        }
        else if (exception is NarrationRightsRequiredException)
        {
            problemDetails.Extensions["code"] = NarrationRightsRequiredException.StableCode;
        }
        else if (exception is SeriesVoicePreviewUnavailableException)
        {
            problemDetails.Title = "Local voice preview is temporarily unavailable";
            problemDetails.Extensions["code"] = SeriesVoicePreviewUnavailableException.StableCode;
        }
        else if (exception is LocalClonePreviewUnavailableException localCloneException)
        {
            problemDetails.Title = "Local clone preview is temporarily unavailable";
            problemDetails.Extensions["code"] = localCloneException.StableCode;
        }
        else if (exception is SpeechSegmentPreviewUnsupportedProviderException)
        {
            problemDetails.Extensions["code"] = SpeechSegmentPreviewUnsupportedProviderException.StableCode;
        }

        httpContext.Response.StatusCode = statusCode;
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails,
            Exception = exception
        });
    }
}
