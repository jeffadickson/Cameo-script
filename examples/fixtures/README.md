These small synthetic fixtures were authored for this repository and use its license.
Names and data are fictional. No redistributed third-party assets are included.

The wrong-name/fractional-visits JSON and negative-hours/invalid-done CSV files exercise
runtime domain validation independently of decoding success. The missing-profile case
deliberately names no fixture, to verify failed acquisition stays a failure.

release.txt exercises whitespace normalization in a pending file future; digest.txt is
exactly the three ASCII bytes abc, without a final newline, for the standard SHA-256
known-answer check in 08-file-futures.cameo.
