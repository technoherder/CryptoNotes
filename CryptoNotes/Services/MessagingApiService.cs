using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace CryptoNotes.Services
{
    /// <summary>
    /// HTTP client for communicating with the CryptoNotes relay server.
    /// All messages sent through this service are already PGP-encrypted.
    /// The server never sees plaintext.
    /// </summary>
    public class MessagingApiService
    {
        private readonly HttpClient _client;
        private string _authToken;
        private string _serverUrl;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        public MessagingApiService()
        {
            _client = new HttpClient();
            _client.Timeout = TimeSpan.FromSeconds(30);
        }

        public void Configure(string serverUrl, string authToken)
        {
            _serverUrl = serverUrl.TrimEnd('/');
            _authToken = authToken;
            _client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", authToken);
        }

        public bool IsConfigured => !string.IsNullOrEmpty(_serverUrl) && !string.IsNullOrEmpty(_authToken);

        /// <summary>
        /// Register a new account with the relay server.
        /// Sends the user's PGP public key for others to discover.
        /// </summary>
        public async Task<ApiResult<LoginResponse>> RegisterAsync(
            string serverUrl, string username, string password, string publicKey)
        {
            var url = $"{serverUrl.TrimEnd('/')}/api/auth/register";
            var payload = new
            {
                username,
                password,
                publicKey
            };

            return await PostAsync<LoginResponse>(url, payload);
        }

        /// <summary>
        /// Log in to an existing account.
        /// </summary>
        public async Task<ApiResult<LoginResponse>> LoginAsync(
            string serverUrl, string username, string password)
        {
            var url = $"{serverUrl.TrimEnd('/')}/api/auth/login";
            var payload = new { username, password };
            return await PostAsync<LoginResponse>(url, payload);
        }

        /// <summary>
        /// Search for users by username.
        /// </summary>
        public async Task<ApiResult<List<UserInfo>>> SearchUsersAsync(string query)
        {
            var url = $"{_serverUrl}/api/users/search?query={Uri.EscapeDataString(query)}";
            return await GetAsync<List<UserInfo>>(url);
        }

        /// <summary>
        /// Get a specific user's public key for encrypting messages to them.
        /// </summary>
        public async Task<ApiResult<UserInfo>> GetUserPublicKeyAsync(string username)
        {
            var url = $"{_serverUrl}/api/users/{Uri.EscapeDataString(username)}/publickey";
            return await GetAsync<UserInfo>(url);
        }

        /// <summary>
        /// Send a PGP-encrypted message to another user.
        /// </summary>
        public async Task<ApiResult<SendResult>> SendMessageAsync(
            string recipientUsername, string encryptedContent)
        {
            var url = $"{_serverUrl}/api/messages/send";
            var payload = new
            {
                recipientUsername,
                encryptedContent
            };
            return await PostAsync<SendResult>(url, payload);
        }

        /// <summary>
        /// Fetch all undelivered messages.
        /// </summary>
        public async Task<ApiResult<List<MessageInfo>>> ReceiveMessagesAsync()
        {
            var url = $"{_serverUrl}/api/messages/receive";
            return await GetAsync<List<MessageInfo>>(url);
        }

        /// <summary>
        /// Get conversation history with a specific user.
        /// </summary>
        public async Task<ApiResult<List<MessageInfo>>> GetConversationAsync(
            string otherUsername, int page = 0)
        {
            var url = $"{_serverUrl}/api/messages/conversation/{Uri.EscapeDataString(otherUsername)}?page={page}";
            return await GetAsync<List<MessageInfo>>(url);
        }

        /// <summary>
        /// Get list of all conversations.
        /// </summary>
        public async Task<ApiResult<List<ConversationInfo>>> GetConversationsAsync()
        {
            var url = $"{_serverUrl}/api/messages/conversations";
            return await GetAsync<List<ConversationInfo>>(url);
        }

        private async Task<ApiResult<T>> GetAsync<T>(string url)
        {
            try
            {
                var response = await _client.GetAsync(url);
                var content = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var data = JsonSerializer.Deserialize<T>(content, JsonOptions);
                    return ApiResult<T>.Success(data);
                }

                return ApiResult<T>.Failure($"Server error: {response.StatusCode}");
            }
            catch (Exception ex)
            {
                return ApiResult<T>.Failure($"Network error: {ex.Message}");
            }
        }

        private async Task<ApiResult<T>> PostAsync<T>(string url, object payload)
        {
            try
            {
                var json = JsonSerializer.Serialize(payload, JsonOptions);
                var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _client.PostAsync(url, httpContent);
                var content = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var data = JsonSerializer.Deserialize<T>(content, JsonOptions);
                    return ApiResult<T>.Success(data);
                }

                return ApiResult<T>.Failure($"Server error: {response.StatusCode} - {content}");
            }
            catch (Exception ex)
            {
                return ApiResult<T>.Failure($"Network error: {ex.Message}");
            }
        }
    }

    public class ApiResult<T>
    {
        public bool IsSuccess { get; private set; }
        public T Data { get; private set; }
        public string Error { get; private set; }

        public static ApiResult<T> Success(T data) =>
            new ApiResult<T> { IsSuccess = true, Data = data };

        public static ApiResult<T> Failure(string error) =>
            new ApiResult<T> { IsSuccess = false, Error = error };
    }

    // DTOs matching server responses
    public class LoginResponse
    {
        public string Token { get; set; }
        public string Username { get; set; }
    }

    public class UserInfo
    {
        public string Username { get; set; }
        public string PublicKey { get; set; }
    }

    public class SendResult
    {
        public int Id { get; set; }
        public string SentAt { get; set; }
    }

    public class MessageInfo
    {
        public int Id { get; set; }
        public string SenderUsername { get; set; }
        public string EncryptedContent { get; set; }
        public string SentAt { get; set; }
    }

    public class ConversationInfo
    {
        public string Username { get; set; }
        public int UnreadCount { get; set; }
        public string LastMessageAt { get; set; }
    }
}
