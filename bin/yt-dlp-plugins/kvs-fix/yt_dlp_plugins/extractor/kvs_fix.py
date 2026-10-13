# Grappler fix for KVS sites (Kernel Video Sharing) on player 15+.
#
# Newer kt_player builds stopped calling their config object `flashvars`
# and give it a random name instead (`var t794d9b6ad4 = {...}`), handing it
# to kt_player(...) as the last argument. yt-dlp only looks for the literal
# `var flashvars =`, so it fails with "Unable to extract flashvars" even
# though the data inside is unchanged.
#
# This finds the name passed to kt_player(), renames it back to `flashvars`,
# and lets yt-dlp's own KVS code do the rest. Pages that still use
# `flashvars` are passed through untouched.
#
# Delete this folder once yt-dlp handles it upstream.

import re

from yt_dlp.extractor.generic import GenericIE


class _KVSPlayerVarIE(GenericIE, plugin_name='kvs_fix'):
    def _extract_kvs(self, url, webpage, video_id):
        if not re.search(r'var\s+flashvars\s*=', webpage):
            name = self._search_regex(
                r'kt_player\s*\(\s*(["\'])[^"\']+\1\s*,[^,]+,[^,]+,[^,]+,\s*([\w$]+)\s*\)',
                webpage, 'kt_player config name', default=None, group=2)
            if name:
                webpage = re.sub(
                    r'var\s+%s\s*=' % re.escape(name), 'var flashvars =', webpage, count=1)
        return super()._extract_kvs(url, webpage, video_id)
