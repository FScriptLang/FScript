# HTTP Runtime integration

`FScript.Runtime` registers HTTP metadata and externs with the default catalog.
`FScript.Language` owns the `Network` capability and evaluator checks. The HTTP
descriptor repeats the permission check before exposing I/O so renamed
descriptors preserve their requirement. Runtime's SDK bridge retains that
requirement when forwarding the default externs.

The interpreter registers the asynchronous operation in its existing task
scope through the SDK bridge. The transport combines scope cancellation with
the per-request deadline. A shared .NET HTTP client pools connections while
each operation owns its request, response, stream, and byte buffer. It reads
only up to one byte beyond the response limit to detect overflow. Automatic
redirects and cookie storage are disabled on the shared client.

`CompiledHttp` exposes the same transport through typed CLR linkage in the
compiled provider registry. Both .NET backends use ordinary FFI linkage with
SDK values at the request and result boundary.

See the [HTTP contract](../specs/http.md) for signatures, configuration and
response semantics.
