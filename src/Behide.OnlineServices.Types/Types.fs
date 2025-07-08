namespace Behide.OnlineServices

/// Unordered pair
type Pair<'T when 'T: comparison> =
    { First: 'T; Second: 'T }

module Pair =
    let create first second =
        { First = min first second
          Second = max first second }

    let isInPair pair value = pair.First = value || pair.Second = value
