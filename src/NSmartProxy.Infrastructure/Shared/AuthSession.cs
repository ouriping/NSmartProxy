using System;

namespace NSmartProxy.Infrastructure
{
    public class AuthSession
    {
        public string UserName { get; set; }
        public string UserId { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsAnonymous { get; set; }
        public bool MustChangePassword { get; set; }
        public DateTime ExpiresUtc { get; set; }
    }

    public interface IAuthSessionStore
    {
        AuthSession GetSession(string token);
        string IssueSession(AuthSession session);
        void RevokeSession(string token);
    }

    public static class AuthSessionLocator
    {
        public static Func<string, AuthSession> Find = token => null;
    }
}
