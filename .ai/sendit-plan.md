# SendIt Task Implementation Plan

This document outlines the plan for implementing the "sendit" task, which involves preparing and submitting a transport declaration to the SPK (System Przesyłek Konduktorskich).

## 1. Project Structure

The implementation will be centered around the `SendItTask` class, with responsibilities delegated to specialized services and tool providers.

```
src/AgentFive/Tasks/SendIt/
|
|- SendItTask.cs            # Main entry point for the task orchestration
|
|- DeclarationService.cs    # Responsible for fetching documentation, parsing it, and building the declaration string.
|
|- SpkClient.cs             # Handles communication with the Hub API (/verify endpoint).
|
|- Tools/
|   |- SpkToolProvider.cs     # Defines the function calling tools available to the agent.
|   |- SpkToolHandler.cs      # Implements the logic for each defined tool.
|
|- Cache/                     # Directory for caching downloaded documentation files.
|
|- Models/
    |- Declaration.cs         # Model representing the fields of the declaration form.
    |- VerificationRequest.cs # DTO for the /verify endpoint payload.
```

## 2. Task Orchestration (`SendItTask.cs`)

The `RunAsync` method in `SendItTask` will be the main orchestrator.

1.  **Initialization:**
    *   Instantiate `OpenRouterService`, `DeclarationService`, and `SpkClient`.
    *   Inject `HubSettings` to get the API key.
    *   Instantiate `SpkToolProvider` and `SpkToolHandler`.

2.  **Agent Loop:**
    *   Create a new chat session with `OpenRouterService`.
    *   Define the initial system prompt, instructing the agent to create a transport declaration for the "sendit" task using the available tools.
    *   The main user prompt will contain the task description.
    *   The agent will use the provided tools (`fetch_text_content`, `fetch_image_content`, `analyze_image_with_vision`, `find_route_code`) to gather information.
    *   The `SpkToolHandler` will process tool calls from the agent.
    *   The loop continues until the agent has all the necessary information to fill the declaration.

3.  **Finalization:**
    *   Once the agent has gathered all data, it will call a final tool, `submit_declaration(declaration_string)`.
    *   `SpkToolHandler` will delegate this call to `SpkClient` to send the final request to the Hub.
    *   Log the response from the Hub.

## 3. Tooling (`Tools/`)

### `SpkToolProvider.cs`
This class will contain the definitions of the tools for the function calling API.

*   `fetch_text_content(url)`: Fetches plain text content from a URL.
*   `fetch_image_content(url)`: Fetches an image from a URL and returns it as a base64 string.
*   `analyze_image_with_vision(image_base64, prompt)`: Sends the image and a specific prompt to the `gpt-5-mini` model with vision capabilities.
*   `find_route_code(origin, destination)`: A dedicated tool to parse route maps/tables and find the correct code.
*   `submit_declaration(declaration_string)`: The final tool to submit the completed declaration.

### `SpkToolHandler.cs`
This class will implement the logic for the tools.

*   It will use `HttpClient` for fetching content.
*   It will manage the local cache in the `Cache/` directory. Before fetching a URL, it will check if the file exists in the cache.
*   The `analyze_image_with_vision` method will call `OpenRouterService` with the appropriate payload for a vision query.
*   `submit_declaration` will call the `SpkClient`.

## 4. Services

### `DeclarationService.cs`
*   This service will be responsible for the core logic of finding and filling the declaration form.
*   It will be called by the agent loop to process the information gathered by the tools.
*   It will contain methods to parse different parts of the documentation (regulations, fee tables, route maps).
*   It will have a method to assemble the final, formatted declaration string based on a template found in the documentation.

### `SpkClient.cs`
*   This service will encapsulate all communication with the `https://hub.ag3nts.org` API.
*   It will have one method: `VerifyDeclarationAsync(string declaration)`.
*   It will construct the JSON payload, including the API key from `HubSettings`, and POST it to the `/verify` endpoint.
*   It will handle basic error logging (logging the full request and response if the status code is not 200 OK).

## 5. Configuration and Models

*   **`HubSettings.cs`**: Will be used to retrieve `HubApiKey` and `HubUrl`.
*   **`OpenRouterSettings.cs`**: Will be used to configure the `OpenRouterService` (API key, model name: `openai/gpt-5-mini`).
*   **Models/**: Simple C# classes/records will be used for DTOs and internal data representation.

## 6. Step-by-Step Implementation Flow

1.  **Setup `SendItTask`**: Implement the basic structure in `SendItTask.cs`, including dependency injection for services.
2.  **Implement `SpkToolProvider`**: Define all the necessary tools as C# objects that can be serialized to the format expected by the OpenAI API.
3.  **Implement `SpkToolHandler`**:
    *   Write the logic for `fetch_text_content` and `fetch_image_content`, including the caching mechanism.
    *   Implement `analyze_image_with_vision` to correctly call the vision model via `OpenRouterService`.
4.  **Develop Agent Loop**: In `SendItTask.cs`, create the main loop that communicates with OpenRouter, passes the tools, and executes the tool calls via `SpkToolHandler`.
5.  **Implement `DeclarationService`**: Write the logic to parse the fetched documentation and assemble the declaration. This will likely be the most complex part.
6.  **Implement `SpkClient`**: Write the final API submission logic.
7.  **Integrate and Test**: Connect all the pieces and run the task. Use detailed logging to debug the agent's reasoning process and the data it gathers.
