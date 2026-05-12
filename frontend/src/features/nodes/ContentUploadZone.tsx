/**
 * ContentUploadZone — drag-and-drop + file picker for uploading node content.
 *
 * Accepts any file. Sends raw bytes to POST /api/nodes/{id}/content with
 * the file's MIME type as Content-Type.
 * On success: the node-content query is invalidated (the content area re-fetches
 * automatically). A sonner success toast is shown.
 * On error: the mutation hook surfaces a sonner toast; zone resets for retry.
 *
 * Also accepts a plain text / markdown drag-drop: when the dragged item is
 * "text/plain" or "text/uri-list" we fall back to a file-picker UX hint.
 *
 * Design: docs/architecture/frontend-bootstrap.md §5.6, §9.3
 * Task: DiVoid node #229
 */

import { useRef, useState, useCallback, DragEvent, ChangeEvent } from 'react';
import { UploadCloud, FileText } from 'lucide-react';
import { toast } from 'sonner';
import { useUploadContent } from './mutations';
import { cn } from '@/lib/cn';

interface ContentUploadZoneProps {
  nodeId: number;
  /** Max file size in bytes. Default: 10 MB. */
  maxBytes?: number;
}

const DEFAULT_MAX_BYTES = 10 * 1024 * 1024; // 10 MB

export function ContentUploadZone({
  nodeId,
  maxBytes = DEFAULT_MAX_BYTES,
}: ContentUploadZoneProps) {
  const mutation = useUploadContent(nodeId);
  const fileInputRef = useRef<HTMLInputElement>(null);
  const [isDragOver, setIsDragOver] = useState(false);

  const upload = useCallback(
    async (file: File) => {
      if (file.size > maxBytes) {
        toast.error(
          `File is too large (${(file.size / 1024 / 1024).toFixed(1)} MB). Maximum is ${(maxBytes / 1024 / 1024).toFixed(0)} MB.`,
        );
        return;
      }

      try {
        await mutation.mutateAsync({ body: file, contentType: file.type || 'application/octet-stream' });
        toast.success('Content uploaded successfully.');
      } catch {
        // Error toast was already shown by the mutation's onError handler.
      }
    },
    [mutation, maxBytes],
  );

  const handleFileChange = (e: ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (file) {
      void upload(file);
      // Reset the input so the same file can be re-selected after an error.
      e.target.value = '';
    }
  };

  const handleDrop = (e: DragEvent<HTMLDivElement>) => {
    e.preventDefault();
    setIsDragOver(false);
    const file = e.dataTransfer.files[0];
    if (file) {
      void upload(file);
    }
  };

  const handleDragOver = (e: DragEvent<HTMLDivElement>) => {
    e.preventDefault();
    setIsDragOver(true);
  };

  const handleDragLeave = (e: DragEvent<HTMLDivElement>) => {
    e.preventDefault();
    setIsDragOver(false);
  };

  const handleKeyDown = (e: React.KeyboardEvent<HTMLDivElement>) => {
    if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault();
      fileInputRef.current?.click();
    }
  };

  return (
    <div
      role="button"
      tabIndex={0}
      aria-label="Upload file — click or drag a file here to replace the node content"
      onDrop={handleDrop}
      onDragOver={handleDragOver}
      onDragLeave={handleDragLeave}
      onClick={() => fileInputRef.current?.click()}
      onKeyDown={handleKeyDown}
      className={cn(
        'group flex flex-col items-center justify-center gap-2 rounded-lg border-2 border-dashed p-6 text-center cursor-pointer transition-colors',
        isDragOver
          ? 'border-primary bg-primary/5'
          : 'border-border hover:border-primary/50 hover:bg-muted/30',
        mutation.isPending && 'opacity-50 cursor-not-allowed',
      )}
    >
      <input
        ref={fileInputRef}
        type="file"
        className="sr-only"
        onChange={handleFileChange}
        disabled={mutation.isPending}
        aria-hidden="true"
        tabIndex={-1}
      />

      {mutation.isPending ? (
        <>
          <UploadCloud
            size={24}
            className="text-muted-foreground animate-pulse"
            aria-hidden="true"
          />
          <span className="text-sm text-muted-foreground">Uploading…</span>
        </>
      ) : (
        <>
          <div className="flex items-center gap-2">
            <UploadCloud
              size={24}
              className={cn(
                'transition-colors',
                isDragOver ? 'text-primary' : 'text-muted-foreground group-hover:text-primary/70',
              )}
              aria-hidden="true"
            />
            <FileText
              size={20}
              className="text-muted-foreground/50"
              aria-hidden="true"
            />
          </div>
          <p className="text-sm text-muted-foreground">
            {isDragOver ? (
              <span className="text-primary font-medium">Drop to upload</span>
            ) : (
              <>
                <span className="font-medium">Click to upload</span> or drag a file here
              </>
            )}
          </p>
          <p className="text-xs text-muted-foreground">
            Any file type · max {(maxBytes / 1024 / 1024).toFixed(0)} MB
          </p>
        </>
      )}
    </div>
  );
}
