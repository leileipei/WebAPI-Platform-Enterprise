# 最终验证输出摘要

源码提交 f862344。完整命令输出在本轮读取确认；本文件保留脱敏的结果摘要。

## 前端9项与构建

```text
✔ write carries cookie CSRF ETag and stable idempotency; csrf refreshed after login (12.123458ms)
✔ 412 exposes conflict and trace without automatic destructive retry (0.345042ms)
✔ resource 403 and hidden-resource 404 trigger authority refresh; 412 retains editing authority (0.606042ms)
✔ ordinary navigation updates accepted path and clean Back works (0.970084ms)
✔ unsaved Back restores original URL without changing rendered page; accepted discard enables Back (0.20475ms)
✔ dirty import blocks sidebar navigation, while successful committed save may navigate (0.101709ms)
✔ resource selectors follow all returned pages and keep existing filters (0.7705ms)
✔ paged selector propagates resource denial instead of returning a partial authorized cache (0.266958ms)
✔ page query replaces an existing page without losing filters (0.061959ms)
ℹ tests 9
ℹ suites 0
ℹ pass 9
ℹ fail 0
ℹ cancelled 0
ℹ skipped 0
ℹ todo 0
ℹ duration_ms 53.893458
vite v6.4.2 building for production...
transforming...
✓ 6280 modules transformed.
rendering chunks...
computing gzip size...
dist/index.html                   0.38 kB │ gzip:  0.30 kB
dist/assets/index-DLQf75HT.css    9.52 kB │ gzip:  2.74 kB
dist/assets/index-CLLihSNP.js   285.93 kB │ gzip: 86.04 kB
✓ built in 2.16s
$ tsc --noEmit && vite build
```

## domain

```text
Passed!  - Failed:     0, Passed:    16, Skipped:     0, Total:    16, Duration: 51 ms - WebApi.Domain.Tests.dll (net10.0)
```

## integration

```text
Passed!  - Failed:     0, Passed:    90, Skipped:     0, Total:    90, Duration: 30 s - WebApi.Integration.Tests.dll (net10.0)
```

## gateway

```text
Passed!  - Failed:     0, Passed:    12, Skipped:     0, Total:    12, Duration: 19 s - WebApi.Gateway.Tests.dll (net10.0)
```

## 真实容器与故障

```text
Build succeeded.
    0 Warning(s)
    0 Error(s)
Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1, Duration: 8 s - WebApi.EndToEnd.Tests.dll (net10.0)
Real container fault checks passed: 7
```

本轮精确项目的容器、卷与秘密文件已核实全部清理。
