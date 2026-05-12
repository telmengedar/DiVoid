import { type ClassValue, clsx } from 'clsx';
import { twMerge } from 'tailwind-merge';

/** Merges Tailwind class names, resolving conflicts correctly. */
export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs));
}
