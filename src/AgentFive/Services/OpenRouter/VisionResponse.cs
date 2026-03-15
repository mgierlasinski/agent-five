using System.Collections.Generic;
using System.Text.Json;

namespace AgentFive.Services.OpenRouter;

public record VisionResponse(List<VisionChoice>? Choices);

public record VisionChoice(VisionMessage? Message);

public record VisionMessage(object Content);