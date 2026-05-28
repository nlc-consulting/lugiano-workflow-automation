import {
  Controller,
  Get,
  Param,
  ParseIntPipe,
  Query,
  Res,
} from '@nestjs/common';
import { ApiTags } from '@nestjs/swagger';
import type { Response } from 'express';
import { UsersService } from './users.service';

// Implements the ra-data-simple-rest contract used by react-admin:
// getList reads ?range=[start,end]&sort=["field","ORDER"] and replies with a
// Content-Range header so the data provider knows the total count.
@ApiTags('users')
@Controller('users')
export class UsersController {
  constructor(private readonly usersService: UsersService) {}

  @Get()
  async findAll(
    @Res({ passthrough: true }) res: Response,
    @Query('range') rangeJson?: string,
    @Query('sort') sortJson?: string,
  ) {
    const [start, end] = parseRange(rangeJson);
    const [field, order] = parseSort(sortJson);
    const take = end - start + 1;

    const { data, total } = await this.usersService.findMany(start, take, field, order);

    const last = Math.min(end, Math.max(total - 1, 0));
    res.set('Content-Range', `users ${start}-${last}/${total}`);
    return data;
  }

  @Get(':id')
  findOne(@Param('id', ParseIntPipe) id: number) {
    return this.usersService.findOne(id);
  }
}

function parseRange(rangeJson?: string): [number, number] {
  try {
    if (rangeJson) {
      const parsed = JSON.parse(rangeJson) as unknown;
      if (
        Array.isArray(parsed) &&
        typeof parsed[0] === 'number' &&
        typeof parsed[1] === 'number'
      ) {
        return [parsed[0], parsed[1]];
      }
    }
  } catch {
    /* fall through to default */
  }
  return [0, 24];
}

const SORTABLE = new Set(['id', 'email', 'fullName', 'role', 'office', 'createdAt']);

function parseSort(sortJson?: string): [string, 'asc' | 'desc'] {
  try {
    if (sortJson) {
      const parsed = JSON.parse(sortJson) as unknown;
      if (Array.isArray(parsed) && typeof parsed[0] === 'string') {
        const field = SORTABLE.has(parsed[0]) ? parsed[0] : 'id';
        const order = String(parsed[1]).toLowerCase() === 'desc' ? 'desc' : 'asc';
        return [field, order];
      }
    }
  } catch {
    /* fall through to default */
  }
  return ['id', 'asc'];
}
