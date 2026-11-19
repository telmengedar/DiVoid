"""Fixed, non-sensitive failures for the package-owned inbox stream."""

class InboxEventError(Exception):
    """Expose only a local fixed code and retry classification."""

    def __init__(self, code: str, permanent: bool = False):
        super().__init__(code)
        self.code = code
        self.permanent = permanent
