using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace GymNotebook.Api;

// Static and stateless on purpose: minting a token is a pure function of (user, secret,
// expiry) with no dependencies to inject. Both /auth/login and, later, /auth/change-password
// call this so a token issued either way has an identical shape.
public static class JwtTokenFactory
{
    public static string CreateToken(User user, string secret, int expiryMinutes)
    {
        // Symmetric (shared-secret) signing: the same Jwt:Secret both signs the token here
        // and verifies it wherever validation happens later. Good enough for a single API
        // that never has to hand out tokens another service must independently verify.
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            // "sub" (subject) is the standard JWT claim for "who this token is about".
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),

            // "tv" (token_version) has no standard claim name — it's this app's own
            // revocation mechanism (see PLAN.md, Auth section). Whatever validates the
            // token later compares this against the user's current TokenVersion in the
            // database; a mismatch means the token was issued before a password change
            // and should be rejected even though it hasn't expired yet.
            new Claim("tv", user.TokenVersion.ToString()),
        };

        var token = new JwtSecurityToken(
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(expiryMinutes),
            signingCredentials: credentials);

        // Serializes the token to the compact "header.payload.signature" string that
        // actually goes over the wire in an Authorization: Bearer header.
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
