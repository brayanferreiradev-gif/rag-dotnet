using RagApi.Agents;
using RagApi.Ai;
using RagApi.Rag;
using RagApi.VectorStore;

namespace RagApi;

/// <summary>
/// Monta as peças do RAG a partir da configuração. Cada peça depende só de uma interface,
/// então trocar de provedor é mudar uma linha no appsettings.json:
///   Llm:Provider        = Ollama | ClaudeCode (sua conta, sem API) | Claude (API)
///   Embeddings:Provider = Ollama | Voyage
///   VectorStore         = Qdrant | InMemory
/// </summary>
public static class ServiceRegistration
{
    public sealed record Providers(string Llm, string LlmModel, string Embeddings, string EmbeddingModel, string VectorStore);

    public static Providers AddRag(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<OllamaOptions>(config.GetSection("Ollama"));
        services.Configure<ClaudeOptions>(config.GetSection("Claude"));
        services.Configure<ClaudeCodeOptions>(config.GetSection("ClaudeCode"));
        services.Configure<VoyageOptions>(config.GetSection("Voyage"));
        services.Configure<QdrantOptions>(config.GetSection("Qdrant"));
        services.Configure<RagOptions>(config.GetSection("Rag"));

        // As chaves também podem vir das variáveis de ambiente "oficiais" de cada empresa.
        services.PostConfigure<ClaudeOptions>(o =>
        {
            if (string.IsNullOrWhiteSpace(o.ApiKey)) o.ApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "";
        });
        services.PostConfigure<VoyageOptions>(o =>
        {
            if (string.IsNullOrWhiteSpace(o.ApiKey)) o.ApiKey = Environment.GetEnvironmentVariable("VOYAGE_API_KEY") ?? "";
        });

        var ollamaUrl = new Uri(config["Ollama:BaseUrl"] ?? "http://localhost:11434");

        // ---- LLM (gera as respostas) ----
        var llm = config["Llm:Provider"] ?? "Ollama";
        string llmModel;
        if (Is(llm, "ClaudeCode"))
        {
            llmModel = config["ClaudeCode:Model"] ?? new ClaudeCodeOptions().Model;
            services.AddSingleton<IChatService, ClaudeCodeChatService>();
        }
        else if (Is(llm, "Claude"))
        {
            llmModel = config["Claude:Model"] ?? new ClaudeOptions().Model;
            services.AddHttpClient<IChatService, ClaudeChatService>(c =>
            {
                c.BaseAddress = new Uri(config["Claude:BaseUrl"] ?? new ClaudeOptions().BaseUrl);
                c.Timeout = TimeSpan.FromMinutes(2);
            });
        }
        else
        {
            llmModel = config["Ollama:ChatModel"] ?? new OllamaOptions().ChatModel;
            services.AddHttpClient<IChatService, OllamaChatService>(c =>
            {
                c.BaseAddress = ollamaUrl;
                c.Timeout = TimeSpan.FromMinutes(5); // LLM local na CPU pode demorar
            });
        }

        // ---- Embeddings (transformam texto em vetor) ----
        var embeddings = config["Embeddings:Provider"] ?? "Ollama";
        string embeddingModel;
        if (Is(embeddings, "Voyage"))
        {
            embeddingModel = config["Voyage:Model"] ?? new VoyageOptions().Model;
            services.AddHttpClient<IEmbeddingService, VoyageEmbeddingService>(c =>
                c.BaseAddress = new Uri(config["Voyage:BaseUrl"] ?? new VoyageOptions().BaseUrl));
        }
        else
        {
            embeddingModel = config["Ollama:EmbeddingModel"] ?? new OllamaOptions().EmbeddingModel;
            services.AddHttpClient<IEmbeddingService, OllamaEmbeddingService>(c =>
            {
                c.BaseAddress = ollamaUrl;
                c.Timeout = TimeSpan.FromMinutes(2);
            });
        }

        // ---- Banco vetorial ----
        var vectorStore = config["VectorStore"] ?? "Qdrant";
        if (Is(vectorStore, "InMemory"))
            services.AddSingleton<IVectorStore, InMemoryVectorStore>();
        else
            services.AddHttpClient<IVectorStore, QdrantVectorStore>(c =>
                c.BaseAddress = new Uri(config["Qdrant:BaseUrl"] ?? "http://localhost:6333"));

        services.AddScoped<RagService>();

        // ---- Agentes: skills (uma por departamento), ferramentas e o loop ----
        services.Configure<AgentOptions>(config.GetSection("Agents"));
        services.AddSingleton<SkillLibrary>();
        services.AddSingleton<StoreData>();
        services.AddScoped<IAgentTool, BuscarDocumentosTool>();
        services.AddScoped<IAgentTool, ConsultarEstoqueTool>();
        services.AddScoped<IAgentTool, ConsultarPedidoTool>();
        services.AddScoped<IAgentTool, AbrirChamadoTool>();
        services.AddScoped<AgentRunner>();

        return new Providers(llm, llmModel, embeddings, embeddingModel, vectorStore);
    }

    private static bool Is(string value, string expected) =>
        value.Equals(expected, StringComparison.OrdinalIgnoreCase);
}
