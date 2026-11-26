import { describe, expect, it } from 'vitest';
import { formatNodeAccess, nodeAccessMask } from './nodeAccess';

describe('node access wire compatibility', () => {
  it.each([
    [0, 'None', 'None'],
    [3, 'Read, Write', 'Global read, Global write'],
    [4, 'OrgRead', 'Organization read'],
    [8, 'OrgWrite', 'Organization write'],
    [12, 'OrgRead, OrgWrite', 'Organization read, Organization write'],
    [15, 'Read, Write, OrgRead, OrgWrite', 'Global read, Global write, Organization read, Organization write'],
  ] as const)('decodes mask %i and enum names into the same labeled grants', (mask, names, label) => {
    expect(nodeAccessMask(names)).toBe(mask);
    expect(nodeAccessMask(mask)).toBe(mask);
    expect(formatNodeAccess(names)).toBe(label);
    expect(formatNodeAccess(mask)).toBe(label);
  });

  it('retains and displays an unnamed integer bit alongside known grants', () => {
    expect(nodeAccessMask(36)).toBe(36);
    expect(formatNodeAccess(36)).toBe('Organization read, Other flags (32)');
  });
});
