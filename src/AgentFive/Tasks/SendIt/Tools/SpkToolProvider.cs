using AgentFive.Services.OpenRouter;

namespace AgentFive.Tasks.SendIt.Tools;

public static class SpkToolProvider
{
	public static List<ChatToolDefinition> CreateTools()
	{
		return new List<ChatToolDefinition>
		{
			new(
				"function",
				new ChatFunctionDefinition(
					"fetch_text_content",
					"Fetches plain text content from a documentation URL. Supports both absolute and relative documentation URLs and caches the response locally.",
					new
					{
						type = "object",
						properties = new
						{
							url = new { type = "string" }
						},
						required = new[] { "url" },
						additionalProperties = false
					})),
			new(
				"function",
				new ChatFunctionDefinition(
					"fetch_image_content",
					"Fetches an image from a documentation URL, caches it locally, and returns its MIME type plus base64 content.",
					new
					{
						type = "object",
						properties = new
						{
							url = new { type = "string" }
						},
						required = new[] { "url" },
						additionalProperties = false
					})),
			new(
				"function",
				new ChatFunctionDefinition(
					"analyze_image_with_vision",
					"Analyzes a previously fetched image with a vision-capable model and returns the model output as text.",
					new
					{
						type = "object",
						properties = new
						{
							imageBase64 = new { type = "string" },
							prompt = new { type = "string" },
							mimeType = new { type = "string" }
						},
						required = new[] { "imageBase64", "prompt" },
						additionalProperties = false
					})),
			new(
				"function",
				new ChatFunctionDefinition(
					"find_route_code",
					"Resolves the best route code for an origin and destination using the documentation, route tables, and excluded-route map when required.",
					new
					{
						type = "object",
						properties = new
						{
							origin = new { type = "string" },
							destination = new { type = "string" }
						},
						required = new[] { "origin", "destination" },
						additionalProperties = false
					})),
			new(
				"function",
				new ChatFunctionDefinition(
					"submit_declaration",
					"Validates the completed declaration and submits it to the hub /verify endpoint. Returns the hub response.",
					new
					{
						type = "object",
						properties = new
						{
							declarationString = new { type = "string" }
						},
						required = new[] { "declarationString" },
						additionalProperties = false
					}))
		};
	}
}