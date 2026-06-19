using System.Text;
using Amazon.S3;
using Amazon.S3.Model;

namespace RagBackend.Api.Services;

public class MinioService
{
    private readonly IAmazonS3 _s3;
    private const string BucketName = "documents";

    public MinioService(IAmazonS3 s3)
    {
        _s3 = s3;
    }

    public async Task EnsureBucketAsync()
    {
        try
        {
            await _s3.GetBucketLocationAsync(BucketName);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            await _s3.PutBucketAsync(new PutBucketRequest
            {
                BucketName = BucketName,
                UseClientRegion = true
            });
        }
    }

    public async Task UploadAsync(Guid documentId, Stream fileStream, string contentType)
    {
        var request = new PutObjectRequest
        {
            BucketName = BucketName,
            Key = documentId.ToString(),
            InputStream = fileStream,
            ContentType = contentType,
            AutoCloseStream = false
        };
        await _s3.PutObjectAsync(request);
    }

    public async Task<string> GetFileBase64Async(Guid documentId)
    {
        var response = await _s3.GetObjectAsync(BucketName, documentId.ToString());
        using var ms = new MemoryStream();
        await response.ResponseStream.CopyToAsync(ms);
        return Convert.ToBase64String(ms.ToArray());
    }
}
