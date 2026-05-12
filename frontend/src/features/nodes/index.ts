/**
 * nodes feature — public API.
 *
 * Exports all hooks and query key factories for node retrieval and mutation.
 * Read paths (PR 2) + Write paths (PR 3).
 */

// Read hooks
export { useNodeList, nodeListQueryKey } from './useNodeList';
export { useNodeListLinkedTo, nodeLinkedToQueryKey } from './useNodeListLinkedTo';
export { useNodePath, nodePathQueryKey } from './useNodePath';
export { useNodeSemantic, nodeSemanticQueryKey } from './useNodeSemantic';
export { useNode, nodeQueryKey } from './useNode';
export { useNodeContent, nodeContentQueryKey } from './useNodeContent';

// Write (mutation) hooks
export {
  useCreateNode,
  usePatchNode,
  useDeleteNode,
  useLinkNodes,
  useUnlinkNodes,
  useUploadContent,
} from './mutations';
export type {
  CreateNodeInput,
  LinkNodesInput,
  UnlinkNodesInput,
  UploadContentInput,
} from './mutations';

// Schemas and status vocabulary
export {
  createNodeSchema,
  editNodeSchema,
  linkSearchSchema,
  statusOptionsForType,
  TASK_STATUSES,
  BUG_STATUSES,
  STATUS_BEARING_TYPES,
} from './schemas';
