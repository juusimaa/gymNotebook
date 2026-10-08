using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace GymNotebook.Api;

// Static and stateless on purpose: callers supply the clock time, so a renewed token
// keeps the original password-proof time while receiving a fresh expiry.
//
// It also mints and reads the *link tokens* that go into emails (specs/002 plan D2): the
// same signed-JWT format, told apart from a session token by a "purpose" claim. The bearer
// handler (Program.cs, OnTokenValidated) refuses any token carrying "purpose", and
// ReadLinkToken refuses any token without the expected one, so a link can never be used as
// a session, a session never as a link, and a confirmation link never as a reset link.
public static class JwtTokenFactory
{
    public const string PurposeClaim = "purpose";
    public const string AuthTimeClaim = "auth_time";

    // Confirmation lasts long enough to survive a weekend away from the inbox; a reset
    // link, which can set a password, as short as is still practical (data-model.md).
    public static readonly TimeSpan VerifyLifetime = TimeSpan.FromHours(48);
    public static readonly TimeSpan ResetLifetime = TimeSpan.FromHours(1);

    public static string CreateToken(User user, string secret, int expiryMinutes,
        DateTimeOffset? now = null, DateTimeOffset? authTime = null)
        => CreateToken(user.Id, user.TokenVersion, secret, expiryMinutes, now, authTime);

    public static string CreateToken(int userId, int tokenVersion, string secret, int expiryMinutes,
        DateTimeOffset? now = null, DateTimeOffset? authTime = null)
    {
        var issuedAt = now ?? TimeProvider.System.GetUtcNow();
        // Symmetric (shared-secret) signing: the same Jwt:Secret both signs the token here
        // and verifies it wherever validation happens later. Good enough for a single API
        // that never has to hand out tokens another service must independently verify.
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            // "sub" (subject) is the standard JWT claim for "who this token is about".
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),

            // "tv" (token_version) has no standard claim name — it's this app's own
            // revocation mechanism (see PLAN.md, Auth section). Whatever validates the
            // token later compares this against the user's current TokenVersion in the
            // database; a mismatch means the token was issued before a password change
            // and should be rejected even though it hasn't expired yet.
            new Claim("tv", tokenVersion.ToString()),
            // Unix seconds are stable across time zones and can be copied on renewal.
            new Claim(AuthTimeClaim, (authTime ?? issuedAt).ToUnixTimeSeconds().ToString()),
            // "iat" (issued at): with "exp" it gives the browser this token's lifetime, so
            // it can renew at half-life (frontend auth/token.ts). Unlike auth_time it is
            // new on every token. JwtSecurityToken's constructor doesn't add it by itself.
            new Claim(JwtRegisteredClaimNames.Iat, issuedAt.ToUnixTimeSeconds().ToString(),
                ClaimValueTypes.Integer64),
        };

        var token = new JwtSecurityToken(
            claims: claims,
            expires: issuedAt.AddMinutes(expiryMinutes).UtcDateTime,
            signingCredentials: credentials);

        // Serializes the token to the compact "header.payload.signature" string that
        // actually goes over the wire in an Authorization: Bearer header.
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    // A link token for `purpose`, valid for that purpose's lifetime from `now`.
    //
    //   verify: sub + email.
    //   reset:  sub + email + tv. Completing a reset bumps TokenVersion, which makes
    //           this tv stale — that is what makes a reset link single-use, with no
    //           token table.
    //
    // Both carry the address, and it is checked again when the link is used: the id
    // alone could, after a database restore rewound the id sequence, name a different
    // account than the one the email went to. With the address too, a link only ever
    // works for the inbox it was sent to.
    public static string CreateLinkToken(User user, LinkPurpose purpose, string secret, DateTimeOffset now)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(PurposeClaim, PurposeName(purpose)),
            new("email", user.Email),
        };
        if (purpose == LinkPurpose.Reset)
        {
            claims.Add(new Claim("tv", user.TokenVersion.ToString()));
        }

        var lifetime = purpose == LinkPurpose.Verify ? VerifyLifetime : ResetLifetime;
        var token = new JwtSecurityToken(
            claims: claims,
            expires: (now + lifetime).UtcDateTime,
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    // Checks a link token in a fixed order: signature first (a forged or altered token is
    // "invalid" whatever else it claims), then purpose, then expiry. Expiry is checked by
    // hand rather than by the validator so that a token for the wrong purpose, or a
    // session token, is always "invalid" — never "expired", which would tell the user to
    // ask for a new link that this token was never going to be.
    //
    // No clock skew: the bearer handler allows five minutes for clocks between servers to
    // disagree, but a link is minted and read by this same API.
    public static LinkTokenResult ReadLinkToken(string? token, LinkPurpose purpose, string secret, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return LinkTokenResult.Invalid;
        }

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = false,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        };

        ClaimsPrincipal principal;
        SecurityToken validated;
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        try
        {
            principal = handler.ValidateToken(token, parameters, out validated);
        }
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException)
        {
            // Bad signature, malformed token, wrong algorithm: all the same to the caller.
            // ArgumentException is what the handler throws for a string that isn't a JWT.
            return LinkTokenResult.Invalid;
        }

        if (principal.FindFirst(PurposeClaim)?.Value != PurposeName(purpose)
            || !int.TryParse(principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out var userId))
        {
            return LinkTokenResult.Invalid;
        }

        // Every link token is minted with an expiry; one without could only be forged,
        // and forging needs the secret, but there's no reason to accept it either way.
        if (validated.ValidTo == DateTime.MinValue)
        {
            return LinkTokenResult.Invalid;
        }
        if (validated.ValidTo <= now.UtcDateTime)
        {
            return LinkTokenResult.Expired;
        }

        _ = int.TryParse(principal.FindFirst("tv")?.Value, out var tokenVersion);
        return new LinkTokenResult(
            LinkTokenStatus.Valid,
            userId,
            principal.FindFirst("email")?.Value,
            tokenVersion);
    }

    private static string PurposeName(LinkPurpose purpose) => purpose switch
    {
        LinkPurpose.Verify => "verify",
        LinkPurpose.Reset => "reset",
        _ => throw new ArgumentOutOfRangeException(nameof(purpose)),
    };
}

// What a link token is for (data-model.md → Link tokens).
public enum LinkPurpose
{
    Verify,
    Reset,
}

public enum LinkTokenStatus
{
    Valid,
    Expired,
    Invalid,
}

// ReadLinkToken's answer. UserId, Email and (for reset) TokenVersion are meaningful
// only when Status is Valid.
public sealed record LinkTokenResult(LinkTokenStatus Status, int UserId, string? Email, int TokenVersion)
{
    public static readonly LinkTokenResult Invalid = new(LinkTokenStatus.Invalid, 0, null, 0);
    public static readonly LinkTokenResult Expired = new(LinkTokenStatus.Expired, 0, null, 0);
}
